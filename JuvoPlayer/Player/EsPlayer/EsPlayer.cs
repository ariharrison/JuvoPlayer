/*!
 * https://github.com/SamsungDForum/JuvoPlayer
 * Copyright 2018, Samsung Electronics Co., Ltd
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
using System.Threading.Tasks;
using ElmSharp;
using JuvoPlayer.Common;
using JuvoLogger;
using JuvoPlayer.Utils;

namespace JuvoPlayer.Player.EsPlayer
{
    public class EsPlayer : IPlayer
    {
        private static readonly ILogger logger = LoggerManager.GetInstance().GetLogger("JuvoPlayer");

        private EsPlayerPacketStorage packetStorage;
        private EsStreamController streamControl;

        public EsPlayer()
            : this(WindowUtils.CreateElmSharpWindow())
        {
        }

        public EsPlayer(Window window)
        {
            try
            {
                packetStorage = new EsPlayerPacketStorage();
                packetStorage.Initialize(StreamType.Audio);
                packetStorage.Initialize(StreamType.Video);

                streamControl = new EsStreamController(packetStorage, window);
                streamControl.Initialize(StreamType.Audio);
                streamControl.Initialize(StreamType.Video);
            }
            catch (InvalidOperationException ioe)
            {
                logger.Error(ioe, "EsPlayer failure");
                throw ioe;
            }
        }

        #region IPlayer Interface Implementation
        public void AppendPacket(Packet packet)
        {
            streamControl.DataIn(packet);
            packetStorage.AddPacket(packet);
        }

        public IObservable<PlayerState> StateChanged()
        {
            return streamControl.StateChanged();
        }

        public IPlayerClient Client
        {
            get => streamControl.Client;
            set => streamControl.Client = value;
        }

        public void Pause()
        {
            logger.Info("");

            streamControl.Pause();
        }

        public void Play()
        {
            logger.Info("");

            streamControl.Play();
        }

        public void Stop()
        {
            logger.Info("");

            streamControl.Stop();
        }

        public Task Seek(TimeSpan time)
        {
            logger.Info("");
            return streamControl.Seek(time);
        }

        public void SetDuration(TimeSpan duration)
        {
            logger.Info("");
            throw new NotImplementedException();
        }

        public void SetPlaybackRate(float rate)
        {
            logger.Info("");
            throw new NotImplementedException();
        }

        public void SetStreamConfig(StreamConfig config)
        {
            logger.Info(config.StreamType().ToString());

            var configPacket = BufferConfigurationPacket.Create(config);

            streamControl.SetStreamConfiguration(configPacket);
        }

        #region IPlayer Interface event callbacks

        public IObservable<string> PlaybackError()
        {
            return streamControl.ErrorOccured();
        }

        public IObservable<TimeSpan> TimeUpdated()
        {
            return streamControl.TimeUpdated();
        }

        public IObservable<bool> BufferingStateChanged()
        {
            return streamControl.BufferingStateChanged();
        }

        public IObservable<DataRequest> DataNeedStateChanged()
        {
            return streamControl.DataNeededStateChanged();
        }


        #endregion
        #endregion

        #region IDisposable Support
        private bool disposedValue; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // Clean packet storage and stream controller
                    logger.Info("StreamController and PacketStorage shutdown");
                    streamControl.Dispose();
                    packetStorage.Dispose();
                }

                disposedValue = true;
            }
        }

        ~EsPlayer()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(false);
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
