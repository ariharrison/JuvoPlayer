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
using System.Collections.Generic;
using JuvoPlayer.Common;
using JuvoLogger;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using ESPlayer = Tizen.TV.Multimedia;
using System.Threading.Tasks;
using ElmSharp;
using JuvoPlayer.Utils;
using Nito.AsyncEx;

namespace JuvoPlayer.Player.EsPlayer
{
    /// <summary>
    /// Controls transfer stream operation
    /// </summary>
    internal sealed class EsStreamController : IDisposable
    {
        private class DataStream
        {
            public EsStream Stream { get; set; }
            public StreamType StreamType { get; set; }
        }

        private readonly ILogger logger = LoggerManager.GetInstance().GetLogger("JuvoPlayer");

        // Prebuffer duration
        private static readonly TimeSpan PreBufferDuration = TimeSpan.FromSeconds(2);

        // Reference to all data streams representing transfer of individual
        // stream data and data storage
        private readonly DataStream[] dataStreams;
        private readonly EsPlayerPacketStorage packetStorage;

        // Reference to ESPlayer & associated window
        private ESPlayer.ESPlayer player;
        private readonly Window displayWindow;
        private readonly bool usesExternalWindow = true;

        // event callbacks
        private readonly Subject<TimeSpan> timeUpdatedSubject = new Subject<TimeSpan>();
        private readonly Subject<string> playbackErrorSubject = new Subject<string>();
        private readonly Subject<Unit> seekCompletedSubject = new Subject<Unit>();
        private readonly Subject<SeekArgs> seekStartedSubject = new Subject<SeekArgs>();
        private readonly Subject<PlayerState> stateChangedSubject = new Subject<PlayerState>();

        // Timer process and supporting cancellation elements for clock extraction
        // and generation
        private Task clockGenerator = Task.CompletedTask;
        private CancellationTokenSource clockGeneratorCts;
        private TimeSpan currentClock;

        // Returns configuration status of all underlying streams.
        // True - all initialized streams are configures
        // False - at least one underlying stream is not configured
        private bool AllStreamsConfigured => dataStreams.All(streamEntry =>
            streamEntry?.Stream.IsConfigured ?? true);

        // Termination & serialization objects for async operations.
        private readonly CancellationTokenSource activeTaskCts = new CancellationTokenSource();
        private readonly AsyncLock asyncOpSerializer = new AsyncLock();

        // Seek ID. Holds seek ID Request starting from one.
        // Used for munching stale packets in data queue until "seek packet" with
        // matching ID is received. This
        private uint seekID;

        private readonly IDisposable[] streamReconfigureSubs;
        private readonly IDisposable[] playbackErrorSubs;

        #region Public API

        public void Initialize(Common.StreamType stream)
        {
            logger.Info(stream.ToString());

            if (dataStreams[(int) stream] != null)
            {
                throw new ArgumentException($"Stream {stream} already initialized");
            }

            // Create new data stream & chunk state entry
            //
            dataStreams[(int) stream] = new DataStream
            {
                Stream = new EsStream(stream, packetStorage),
                StreamType = stream
            };

            dataStreams[(int) stream].Stream.SetPlayer(player);
            streamReconfigureSubs[(int) stream] = dataStreams[(int) stream].Stream.StreamReconfigure()
                .Subscribe(unit => OnStreamReconfigure(), SynchronizationContext.Current);
            playbackErrorSubs[(int) stream] = dataStreams[(int) stream].Stream.PlaybackError()
                .Subscribe(OnEsStreamError, SynchronizationContext.Current);
        }

        public EsStreamController(EsPlayerPacketStorage storage)
            : this(storage,
                WindowUtils.CreateElmSharpWindow())
        {
            usesExternalWindow = false;
        }

        public EsStreamController(EsPlayerPacketStorage storage, Window window)
        {
            displayWindow = window;

            player = new ESPlayer.ESPlayer();

            player.Open();
            player.SetTrustZoneUse(true);
            player.SetDisplay(displayWindow);

            packetStorage = storage;

            // Create placeholder to data streams & chunk states
            dataStreams = new DataStream[(int) StreamType.Count];
            streamReconfigureSubs = new IDisposable[(int) StreamType.Count];
            playbackErrorSubs = new IDisposable[(int) StreamType.Count];

            //attach event handlers
            player.EOSEmitted += OnEos;
            player.ErrorOccurred += OnESPlayerError;
            player.BufferStatusChanged += OnBufferStatusChanged;
        }

        /// <summary>
        /// Sets provided configuration to appropriate stream.
        /// </summary>
        /// <param name="config">StreamConfig</param>
        public void SetStreamConfiguration(BufferConfigurationPacket configPacket)
        {
            var streamType = configPacket.StreamType;

            logger.Info($"{streamType}:");

            try
            {
                var pushResult = dataStreams[(int) streamType].Stream.SetStreamConfig(configPacket);

                // Configuration queued. Do not prepare stream :)
                if (pushResult == EsStream.SetStreamConfigResult.ConfigQueued)
                    return;

                // Check if all initialized streams are configured
                if (!AllStreamsConfigured)
                    return;

                var token = activeTaskCts.Token;
                StreamPrepare(token);
            }
            catch (NullReferenceException)
            {
                // packetQueue can hold ALL StreamTypes, but not all of them
                // have to be supported.
                logger.Warn($"Uninitialized Stream Type {streamType}");
            }
            catch (OperationCanceledException)
            {
                logger.Info($"{streamType}: Operation Cancelled");
            }
            catch (ObjectDisposedException)
            {
                logger.Info($"{streamType}: Operation Cancelled and disposed");
            }
            catch (InvalidOperationException)
            {
                // Queue has been marked as completed
                logger.Warn($"Data queue terminated for stream: {streamType}");
            }
            catch (UnsupportedStreamException use)
            {
                logger.Error(use, $"{streamType}");
                OnEsStreamError(use.Message);
            }
        }

        /// <summary>
        /// Starts playback on all initialized streams. Streams do have to be
        /// configured in order for the call to start playback.
        /// </summary>
        public void Play()
        {
            logger.Info("");

            if (!AllStreamsConfigured)
                throw new InvalidOperationException("Initialized streams are not configured. Play Aborted");

            try
            {
                var state = player.GetState();
                switch (state)
                {
                    case ESPlayer.ESPlayerState.Playing:
                        return;
                    case ESPlayer.ESPlayerState.Ready:
                        player.Start();
                        break;
                    case ESPlayer.ESPlayerState.Paused:
                        player.Resume();
                        break;
                    default:
                        throw new InvalidOperationException($"Play called in invalid state: {state}");
                }

                EnableTransfer();
                StartClockGenerator();
                stateChangedSubject.OnNext(PlayerState.Playing);
            }
            catch (InvalidOperationException ioe)
            {
                logger.Error(ioe);
            }
        }

        /// <summary>
        /// Pauses playback on all initialized streams. Playback had to be played.
        /// </summary>
        public void Pause()
        {
            logger.Info("");

            try
            {
                DisableTransfer();
                player.Pause();
                StopClockGenerator();
                stateChangedSubject.OnNext(PlayerState.Paused);
            }
            catch (InvalidOperationException ioe)
            {
                logger.Error(ioe);
            }
        }

        /// <summary>
        /// Stops playback on all initialized streams.
        /// </summary>
        public void Stop()
        {
            logger.Info("");

            var state = player.GetState();
            if (state != ESPlayer.ESPlayerState.Paused && state != ESPlayer.ESPlayerState.Playing)
                return;

            try
            {
                DisableTransfer();
                player.Stop();
                StopClockGenerator();
                stateChangedSubject.OnNext(PlayerState.Idle);
            }
            catch (InvalidOperationException ioe)
            {
                logger.Error(ioe);
            }
        }

        public async void Seek(TimeSpan time)
        {
            logger.Info("");

            ++seekID;

            var token = activeTaskCts.Token;

            await SeekStreamInitialize(token);

            seekStartedSubject.OnNext(new SeekArgs {Id = seekID, Position = time});

            await StreamSeek(time, token);
        }

        #endregion

        #region Private Methods

        #region Internal EsPlayer event handlers

        private void OnStreamReconfigure()
        {
            logger.Info("");

            try
            {
                var token = activeTaskCts.Token;
                RestartPlayer(token);
            }
            catch (OperationCanceledException)
            {
                logger.Info("Operation canceled");
            }
            catch (ObjectDisposedException)
            {
                logger.Info("Operation cancelled and disposed");
            }
        }

        #endregion

        #region ESPlayer event handlers

        private void OnBufferStatusChanged(object sender, ESPlayer.BufferStatusEventArgs buffArgs)
        {
            var juvoStream = buffArgs.StreamType.JuvoStreamType();
            var state = buffArgs.BufferStatus == ESPlayer.BufferStatus.Overrun
                ? BufferState.BufferOverrun
                : BufferState.BufferUnderrun;

            if (state == BufferState.BufferUnderrun)
                dataStreams[(int) juvoStream].Stream.Wakeup();
        }

        /// <summary>
        /// ESPlayer event handler. Notifies that ALL played streams have
        /// completed playback (EOS was sent on all of them)
        /// Methods
        /// </summary>
        /// <param name="sender">Object</param>
        /// <param name="eosArgs">ESPlayer.EosArgs</param>
        private void OnEos(object sender, ESPlayer.EOSEventArgs eosArgs)
        {
            logger.Info(eosArgs.ToString());

            // Stop and disable all initialized data streams.
            DisableTransfer();
            DisableInput();

            stateChangedSubject.OnCompleted();
        }

        /// <summary>
        /// ESPlayer event handler. Notifies of an error condition during
        /// playback.
        /// Stops and disables all initialized streams and notifies of an error condition
        /// through PlaybackError event.
        /// </summary>
        /// <param name="sender">Object</param>
        /// <param name="errorArgs">ESPlayer.ErrorArgs</param>
        private void OnESPlayerError(object sender, ESPlayer.ErrorEventArgs errorArgs)
        {
            var error = errorArgs.ToString();

            logger.Error(error);

            // Stop and disable all initialized data streams.
            DisableTransfer();
            DisableInput();

            // Perform error notification
            playbackErrorSubject.OnNext(error);
        }

        private void OnEsStreamError(string error)
        {
            logger.Error(error);

            // Stop and disable all initialized data streams.
            DisableTransfer();
            DisableInput();

            // Perform error notification
            playbackErrorSubject.OnNext(error);
        }

        public IObservable<string> ErrorOccured()
        {
            return playbackErrorSubject.AsObservable();
        }

        /// <summary>
        /// ESPlayer event handler. Issued after calling AsyncPrepare. Stream type
        /// passed as an argument indicates stream for which data transfer has be started.
        /// This effectively starts playback.
        /// </summary>
        /// <param name="esPlayerStreamType">ESPlayer.StreamType</param>
        private async void OnReadyToStartStream(ESPlayer.StreamType esPlayerStreamType)
        {
            var streamType = esPlayerStreamType.JuvoStreamType();

            logger.Info(streamType.ToString());

            dataStreams[(int) streamType].Stream.Start();

            logger.Info($"{streamType}: Completed");

            await Task.Yield();
        }

        private async void OnReadyToSeekStream(ESPlayer.StreamType esPlayerStreamType, TimeSpan time)
        {
            logger.Info($"{esPlayerStreamType}: {time}");
            OnReadyToStartStream(esPlayerStreamType);

            await Task.Yield();
        }

        #endregion

        public IObservable<TimeSpan> TimeUpdated()
        {
            return timeUpdatedSubject.AsObservable();
        }

        public IObservable<Unit> SeekCompleted()
        {
            return seekCompletedSubject.AsObservable();
        }

        private async Task Prebuffer(CancellationToken token)
        {
            try
            {
                bool prebuffer;

                do
                {
                    prebuffer = false;

                    foreach (var esStream in dataStreams.Where(esStream => esStream != null))
                    {
                        var storedDuration = packetStorage.Duration(esStream.StreamType);
                        logger.Info($"{esStream.StreamType}: Prebuffering {storedDuration}/{PreBufferDuration}");
                        if (storedDuration < PreBufferDuration)
                            prebuffer = true;
                    }

                    if (prebuffer)
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                } while (prebuffer);
            }
            catch (TaskCanceledException)
            {
                logger.Info("Operation cancelled");
                throw new OperationCanceledException();
            }
        }

        /// <summary>
        /// Method executes PrepareAsync on ESPlayer. On success, notifies
        /// event PlayerInitialized. At this time player is ALREADY PLAYING
        /// </summary>
        /// <returns>bool
        /// True - AsyncPrepare
        /// </returns>
        private async Task StreamPrepare(CancellationToken token)
        {
            logger.Info("");

            try
            {
                using (await asyncOpSerializer.LockAsync(token))
                {
                    await Prebuffer(token);

                    logger.Info("Player.PrepareAsync()");
                    await player.PrepareAsync(OnReadyToStartStream).WithCancellation(token);

                    logger.Info("Starting Playback");
                    StartClockGenerator();

                    stateChangedSubject.OnNext(PlayerState.Prepared);
                }
            }
            catch (InvalidOperationException ioe)
            {
                logger.Error(ioe);
                playbackErrorSubject.OnNext(ioe.Message);
            }
            catch (OperationCanceledException)
            {
                logger.Info("Operation Cancelled");
                DisableTransfer();
            }
            catch (Exception e)
            {
                logger.Error(e);
                playbackErrorSubject.OnNext("Start Failed");
            }
        }

        /// <summary>
        /// Completes data streams.
        /// </summary>
        /// <returns>List<Task> List of data streams being terminated</returns>
        private List<Task> GetActiveTasks()
        {
            logger.Info("");
            var awaitables = new List<Task>(
                dataStreams.Where(esStream => esStream != null)
                    .Select(esStream => esStream.Stream.GetActiveTask()));

            return awaitables;
        }

        private async Task RestartPlayer(CancellationToken token)
        {
            logger.Info("");

            try
            {
                using (await asyncOpSerializer.LockAsync(token))
                {
                    // Stop data streams & clock
                    DisableTransfer();

                    // Collect enough data to restart transfer
                    await Prebuffer(token);

                    StopClockGenerator();

                    // Stop any underlying async ops
                    var terminations = GetActiveTasks();
                    terminations.Add(clockGenerator);

                    logger.Info($"Waiting for completion of {terminations.Count} activities");
                    await Task.WhenAll(terminations).WithCancellation(token);

                    token.ThrowIfCancellationRequested();

                    logger.Info("Restarting ESPlayer");
                    player.Stop();
                    player.Dispose();

                    player = new ESPlayer.ESPlayer();
                    player.Open();
                    player.SetTrustZoneUse(true);
                    player.SetDisplay(displayWindow);

                    logger.Info("Setting new stream configuration");

                    foreach (var esStream in dataStreams.Where(esStream => esStream != null))
                    {
                        esStream.Stream.SetPlayer(player);
                        esStream.Stream.ResetStreamConfig();
                    }

                    logger.Info("Player.PrepareAsync()");
                    await player.PrepareAsync(OnReadyToStartStream).WithCancellation(token);

                    logger.Info("Player.PrepareAsync() Completed");

                    // TODO: Do we always want to start player after restart?
                    Play();
                }
            }
            catch (OperationCanceledException)
            {
                logger.Info("Operation Cancelled");
                DisableTransfer();
            }
            catch (Exception e)
            {
                logger.Error(e);
                playbackErrorSubject.OnNext("Restart Error");
            }
        }

        private Task SeekStreamInitialize(CancellationToken token)
        {
            logger.Info("");
            // Stop data streams. They will be restarted from
            // SeekAsync handler.
            DisableTransfer();
            StopClockGenerator();
            // Make sure data transfer is stopped!
            // SeekAsync behaves unpredictably when data transfer to player
            // is occuring while SeekAsync gets called
            var terminations = GetActiveTasks();
            terminations.Add(clockGenerator);

            logger.Info($"Waiting for completion of {terminations.Count} activities");

            return Task.WhenAll(terminations).WithCancellation(token);
        }

        private async Task StreamSeek(TimeSpan time, CancellationToken token)
        {
            logger.Info(time.ToString());

            // TODO: Propagate exceptions to upper layers
            try
            {
                using (await asyncOpSerializer.LockAsync(token))
                {
                    logger.Info("Seeking Streams");
                    var seekOperations = new List<Task<EsStream.SeekResult>>();

                    foreach (var esStream in dataStreams.Where(esStream => esStream != null))
                        seekOperations.Add(esStream.Stream.Seek(seekID, time, token));

                    await Task.WhenAll(seekOperations).WithCancellation(token);

                    // Check if any task encountered destructive config change
                    if (seekOperations.Any(seekTask => seekTask.Result == EsStream.SeekResult.RestartRequired))
                    {
                        // Restart needed
                        logger.Info("Configuration change during seek. Restarting Player");
                        RestartPlayer(token);
                        return;
                    }

                    await Prebuffer(token);

                    logger.Info("Player.SeekAsync()");

                    await player.SeekAsync(time, OnReadyToSeekStream).WithCancellation(token);

                    logger.Info("Player.SeekAsync() Completed");
                    StartClockGenerator();
                }
            }
            catch (OperationCanceledException)
            {
                logger.Info("Operation Cancelled");
            }
            catch (Exception e)
            {
                logger.Error(e);
                playbackErrorSubject.OnNext("Seek Failed");
            }
            finally
            {
                // Always notify UI on seek end regardless of seek status
                // to unblock it for further seeks ops.
                // TODO: Remove SeekCompleted event. Return Task to PlayerController/PlayerService.
                if (!token.IsCancellationRequested)
                    seekCompletedSubject.OnNext(Unit.Default);
            }
        }

        /// <summary>
        /// Stops all initialized data streams preventing transfer of data from associated
        /// data queue to underlying player. When stopped, stream can still accept new data
        /// </summary>
        private void DisableTransfer()
        {
            logger.Info("Stopping all data streams");

            foreach (var esStream in dataStreams.Where(esStream => esStream != null))
                esStream.Stream.Stop();
        }

        /// <summary>
        /// Starts all initialized data streams allowing transfer of data from associated
        /// data queues to underlying player
        /// </summary>
        /// <param name="startTransfer">
        /// True - Start Transfer
        /// False - Enable transfer but do not start it</param>
        private void EnableTransfer()
        {
            logger.Info("");

            // Starts can happen.. when they happen. See no reason to
            // wait for their completion.
            foreach (var esStream in dataStreams.Where(esStream => esStream != null))
                esStream.Stream.Start();
        }

        /// <summary>
        /// Disables all initialized data streams preventing
        /// any further new input collection
        /// </summary>
        private void DisableInput()
        {
            logger.Info("Stop and Disable all data streams");

            foreach (var esStream in dataStreams.Where(esStream => esStream != null))
                esStream.Stream.Disable();
        }

        /// <summary>
        /// Time generation task. Time is generated from ESPlayer OR auto generated.
        /// Auto generation handles current representation change mechanism where
        /// full stop of ESPlayer is required and no new times are available.
        /// </summary>
        /// <returns>Task</returns>
        private async Task GenerateTimeUpdates(CancellationToken token)
        {
            logger.Info($"Clock extractor: Started");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        player.GetPlayingTime(out var currentPlayTime);

                        currentClock = currentPlayTime;
                        timeUpdatedSubject.OnNext(currentClock);
                    }
                    catch (InvalidOperationException ioe)
                    {
                        logger.Warn(ioe, "Cannot obtain play time from player");
                    }

                    await Task.Delay(500, token);
                }
            }
            catch (TaskCanceledException)
            {
                logger.Info("Operation Cancelled");
            }
            catch (Exception e)
            {
                // Invoking "external" code through TimeUpdate event. Catch any exceptions
                // and display info to ease debugging
                logger.Info(e.Message);
                logger.Info(e.Source);
                logger.Info(e.StackTrace);
                playbackErrorSubject.OnNext("Playback Error");
            }
        }

        /// <summary>
        /// Starts clock generation task
        /// </summary>
        private void StartClockGenerator()
        {
            logger.Info("");

            if (!clockGenerator.IsCompleted)
            {
                logger.Warn($"Clock generator running: {clockGenerator.Status}");
                return;
            }

            clockGeneratorCts?.Dispose();
            clockGeneratorCts = new CancellationTokenSource();
            var stopToken = clockGeneratorCts.Token;

            // Start time updater
            clockGenerator = Task.Run(() => GenerateTimeUpdates(stopToken), stopToken);
        }

        /// <summary>
        /// Terminates clock generation task
        /// </summary>
        private void StopClockGenerator()
        {
            logger.Info("");

            if (clockGenerator.IsCompleted)
            {
                logger.Warn($"Clock generator not running: {clockGenerator.Status}");
                return;
            }

            clockGeneratorCts.Cancel();
        }

        #endregion

        #region Dispose support

        private bool isDisposed;

        private void TerminateAsyncOperations()
        {
            // Stop clock & async operations
            logger.Info("Clock/AsyncOps shutdown");
            activeTaskCts.Cancel();

            StopClockGenerator();
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            logger.Info("Stopping playback");
            try
            {
                player.Stop();
            }
            catch (InvalidOperationException)
            {
                // Ignore. Will be raised if not playing :)
            }

            logger.Info("Data Streams shutdown");
            // Stop data streams
            DisableTransfer();

            DetachEventHandlers();

            TerminateAsyncOperations();

            ShutdownStreams();

            DisposeAllSubjects();

            DisposeAllSubscriptions();

            // Shut down player
            logger.Info("ESPlayer shutdown");

            // Don't call Close. Dispose does that. Otherwise exceptions will fly
            player.Dispose();
            if (usesExternalWindow == false)
                WindowUtils.DestroyElmSharpWindow(displayWindow);

            // Clean up internal object
            activeTaskCts.Dispose();
            clockGeneratorCts?.Dispose();

            isDisposed = true;
        }

        private void ShutdownStreams()
        {
            // Dispose of individual streams.
            logger.Info("Data Streams shutdown");
            foreach (var esStream in dataStreams.Where(esStream => esStream != null))
                esStream.Stream.Dispose();
        }

        private void DetachEventHandlers()
        {
            // Detach event handlers
            logger.Info("Detaching event handlers");

            player.EOSEmitted -= OnEos;
            player.ErrorOccurred -= OnESPlayerError;
            player.BufferStatusChanged -= OnBufferStatusChanged;
        }

        private void DisposeAllSubjects()
        {
            playbackErrorSubject.Dispose();
            seekCompletedSubject.Dispose();
            seekStartedSubject.Dispose();
            timeUpdatedSubject.Dispose();
        }

        private void DisposeAllSubscriptions()
        {
            foreach (var streamReconfigureSub in streamReconfigureSubs)
                streamReconfigureSub?.Dispose();
            foreach (var playbackErrorSub in playbackErrorSubs)
                playbackErrorSub?.Dispose();
        }

        #endregion

        public IObservable<SeekArgs> SeekStarted()
        {
            return seekStartedSubject.AsObservable();
        }

        public IObservable<PlayerState> StateChanged()
        {
            return stateChangedSubject.AsObservable();
        }
    }
}