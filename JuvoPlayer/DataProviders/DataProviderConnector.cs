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
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using JuvoPlayer.Player;
using JuvoPlayer.Common;

namespace JuvoPlayer.DataProviders
{
    public class DataProviderConnector : IDisposable
    {
        private class PlayerClient : IPlayerClient
        {
            private readonly IDataProvider dataProvider;
            private readonly DataProviderConnector connector;

            public PlayerClient(DataProviderConnector owner, IDataProvider provider)
            {
                connector = owner;
                dataProvider = provider;
            }

            public async Task<TimeSpan> Seek(TimeSpan position, CancellationToken token)
            {
                try
                {
                    connector.DisconnectPlayerController();
                    connector.DisconnectDataProvider();
                    return await dataProvider.Seek(position, token);
                }
                finally
                {
                    connector.ConnectPlayerController();
                    connector.ConnectDataProvider();
                }
            }
        }

        private CompositeDisposable dataProviderSubscriptions;
        private CompositeDisposable playerControllerSubscriptions;
        private IPlayerClient client;
        private readonly IPlayerController playerController;
        private readonly IDataProvider dataProvider;
        private readonly SynchronizationContext context;

        public DataProviderConnector(IPlayerController playerController, IDataProvider dataProvider,
            SynchronizationContext context = null)
        {
            this.playerController = playerController ?? throw new ArgumentNullException(nameof(playerController), "Player controller cannot be null");
            this.dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider), "Data provider cannot be null");
            this.context = context ?? SynchronizationContext.Current;
            Connect();
        }

        private void Connect()
        {
            ConnectDataProvider();
            ConnectPlayerController();
            InstallPlayerClient();
        }

        private void InstallPlayerClient()
        {
            client = new PlayerClient(this, dataProvider);
            playerController.Client = client;
        }

        private void ConnectDataProvider()
        {
            dataProviderSubscriptions = new CompositeDisposable
            {
                playerController.TimeUpdated().Subscribe(dataProvider.OnTimeUpdated, context),
                playerController.StateChanged().Subscribe(dataProvider.OnStateChanged, context),
                playerController.DataStateChanged().Subscribe(dataProvider.OnDataStateChanged,context),
                playerController.BufferingStateChanged().Subscribe(dataProvider.OnBufferingStateChanged,context)
            };
        }

        private void DisconnectDataProvider()
        {
            dataProviderSubscriptions?.Dispose();
        }

        private void ConnectPlayerController()
        {
            playerControllerSubscriptions = new CompositeDisposable
            {
                dataProvider.ClipDurationChanged()
                    .Subscribe(playerController.OnClipDurationChanged, context),
                dataProvider.DRMInitDataFound()
                    .Subscribe(playerController.OnDRMInitDataFound, context),
                dataProvider.SetDrmConfiguration()
                    .Subscribe(playerController.OnSetDrmConfiguration, context),
                dataProvider.StreamConfigReady()
                    .Subscribe(playerController.OnStreamConfigReady, context),
                dataProvider.PacketReady()
                    .Subscribe(playerController.OnPacketReady, context),
                dataProvider.StreamError()
                    .Subscribe(playerController.OnStreamError, context),
                dataProvider.BufferingStateChanged()
                    .Subscribe(playerController.OnBufferingStateChanged,context)
            };
        }

        private void DisconnectPlayerController()
        {
            playerControllerSubscriptions?.Dispose();
        }

        public void Dispose()
        {
            DisconnectPlayerController();
            DisconnectDataProvider();
        }
    }
}
