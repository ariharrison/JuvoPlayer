﻿/*!
 * https://github.com/SamsungDForum/JuvoPlayer
 * Copyright 2019, Samsung Electronics Co., Ltd
 * Licensed under the MIT license
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using JuvoLogger;
using JuvoPlayer.Common;
using static Configuration.StreamBufferControllerConfig;
using static JuvoPlayer.Player.EsPlayer.Stream.Buffering.StreamBufferEvents;

namespace JuvoPlayer.Player.EsPlayer.Stream.Buffering
{
    internal static class StreamBufferEvents
    {
        [Flags]
        public enum StreamBufferEvent
        {
            None = 0,
            Buffering = 1,
            DataRequest = 2,
            All = Buffering | DataRequest
        }
    }

    // TODO: Extend class functionality to provide:
    // TODO:    - Prebuffering on stream start (initial) to accomodate
    // TODO:      MPD's minBufferTime if present. minBufferTime = minimum buffer period
    // TODO:      before playback start assuring smooth content delivery as defined in
    // TODO:      ISO/IEC 23009-1 5.3.1.2 Table 3

    internal class StreamBufferController : IDisposable
    {
        private readonly ILogger logger = LoggerManager.GetInstance().GetLogger("JuvoPlayer");

        private readonly StreamBuffer[] streamBuffers = new StreamBuffer[(int)StreamType.Count];
        private readonly MetaDataStreamConfig[] metaDataConfigs = new MetaDataStreamConfig[(int)StreamType.Count];

        private readonly Subject<(bool bufferingNeeded, bool allowPublication)> bufferingSubject = new Subject<(bool bufferingNeeded, bool allowPublication)>();
        private readonly Subject<DataRequest> dataRequestSubject = new Subject<DataRequest>();
        private readonly Subject<DataRequest> bufferConfigurationSubject = new Subject<DataRequest>();

        private StreamBufferEvent allowedEventsFlag = StreamBufferEvent.All;

        private Task bufferStatePublisherTask;
        private CancellationTokenSource publisherCts;

        private readonly IDisposable playerStateSubscription;
        private PlayerState playerState;

        public IObservable<bool> BufferingStateChanged() =>
            bufferingSubject
                .DistinctUntilChanged((bufferEvent) => bufferEvent.bufferingNeeded)
                .Where(bufferingEvent => bufferingEvent.allowPublication &&
                                         (playerState == PlayerState.Playing ||
                                         playerState == PlayerState.Paused))
                .Select(bufferingEvent => bufferingEvent.bufferingNeeded)
                .Do(state => logger.Info($"Buffering Needed: {state}"))
                .AsObservable();

        public IObservable<DataRequest> DataNeededStateChanged() =>
            dataRequestSubject
                .Merge(bufferConfigurationSubject)
                .AsObservable();

        public StreamBuffer GetStreamBuffer(StreamType stream)
            => streamBuffers[(int)stream];

        public void PublishBufferState()
        {
            PushNotifications(StreamBufferEvent.Buffering, bufferConfigurationSubject);
        }

        public void Initialize(StreamType stream)
        {
            logger.Info(stream.ToString());

            var index = (int)stream;

            if (streamBuffers[index] != null)
                throw new ArgumentException($"Stream buffer {stream} already initialized");

            streamBuffers[index] = new StreamBuffer(stream);

            if (bufferStatePublisherTask != null) return;
            publisherCts = new CancellationTokenSource();
            bufferStatePublisherTask = Task.Run(async () => await GenerateBufferUpdates(publisherCts.Token));
        }

        public StreamBufferController(IObservable<PlayerState> playerStateSubject)
        {
            ResetStreamBufferingState();

            playerStateSubscription =
                playerStateSubject.Subscribe(state => playerState = state, SynchronizationContext.Current);
        }

        public void DataIn(Packet packet)
        {
            var index = (int)packet.StreamType;

            if (!packet.ContainsData())
            {
                if (packet is EOSPacket)
                    streamBuffers[index].MarkEosDts();

                return;
            }

            streamBuffers[index].DataIn(StreamBuffer.GetStreamClock(packet));
        }

        public void DataOut(Packet packet)
        {
            if (!packet.ContainsData())
                return;

            streamBuffers[(int)packet.StreamType].DataOut(StreamBuffer.GetStreamClock(packet));
        }

        private void PushNotifications(StreamBufferEvent pushRequest, IObserver<DataRequest> pipeline = null, StreamType forStream = StreamType.Count)
        {
            if (allowedEventsFlag == StreamBufferEvent.None)
                return;

            var bufferState = streamBuffers
                 .Where(stream => stream != null && (stream.StreamType == forStream || forStream == StreamType.Count))
                 .Select(buffer => buffer.GetDataRequest()).ToList();

            if (StreamBufferEvent.DataRequest == (allowedEventsFlag & pushRequest & StreamBufferEvent.DataRequest))
            {
                if (pipeline == null)
                    pipeline = dataRequestSubject;

                logger.Debug($"{bufferState.Aggregate("", (s, args) => s + "\n\t" + args.ToString())}");
                foreach (var dataArg in bufferState)
                {
                    pipeline.OnNext(dataArg);
                }
            }

            if (StreamBufferEvent.Buffering == (allowedEventsFlag & pushRequest & StreamBufferEvent.Buffering))
            {
                var bufferingNeeded = bufferState.Any(dataArg => dataArg.IsBufferEmpty);

                bufferingSubject.OnNext((bufferingNeeded, true));
            }
        }

        private void ResetStreamBufferingState() =>
            bufferingSubject.OnNext((true, false));

        public void EnableEvents(StreamBufferEvent events)
        {
            allowedEventsFlag = events;

            logger.Info(allowedEventsFlag.ToString());
        }

        public void ReportFullBuffer()
        {
            foreach (var buffer in streamBuffers)
                buffer?.ReportFullBuffer();
        }

        public void ReportActualBuffer()
        {
            foreach (var buffer in streamBuffers)
                buffer?.ReportActualBuffer();
        }

        public void SetMetaDataConfiguration(MetaDataStreamConfig config)
        {
            var index = (int)config.Stream;

            if (config == metaDataConfigs[index])
                return;

            var stream = streamBuffers[index];

            metaDataConfigs[index] = config;
            stream.UpdateBufferConfiguration(config);

            PushNotifications(StreamBufferEvent.DataRequest, bufferConfigurationSubject, config.Stream);
        }

        public void ResetBuffers()
        {
            ResetStreamBufferingState();

            foreach (var buffer in streamBuffers)
                buffer?.Reset();
        }

        private async Task GenerateBufferUpdates(CancellationToken token)
        {
            logger.Info("Buffer update generator started");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(EventGenerationInterval, token);
                    PushNotifications(StreamBufferEvent.All);
                }
                catch (TaskCanceledException)
                {
                    logger.Info("Operation Cancelled");
                }
            }
            logger.Info("Buffer update generator terminated");
        }

        public void Dispose()
        {
            logger.Info("");

            playerStateSubscription.Dispose();
            publisherCts?.Cancel();
            publisherCts?.Dispose();
            bufferingSubject.Dispose();
            dataRequestSubject.Dispose();
            bufferConfigurationSubject.Dispose();
        }
    }
}
