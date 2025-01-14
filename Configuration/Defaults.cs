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

namespace Configuration
{
    public static class SeekLogic
    {
        public static TimeSpan DefaultSeekInterval { get; set; } = TimeSpan.FromSeconds(5);
        public static TimeSpan DefaultSeekAccumulateInterval { get; set; } = TimeSpan.FromSeconds(2);
        public static double DefaultMaximumSeekIntervalPercentOfContentTotalTime { get; set; } = 1.0;

        public static TimeSpan DefaultSeekIntervalValueThreshold { get; set; } =
                TimeSpan.FromMilliseconds(200); // time between key events when key is being hold is ~100ms   
    }

    public static class StreamBufferControllerConfig
    {
        public static TimeSpan EventGenerationInterval { get; set; } = TimeSpan.FromSeconds(1);
    }

    public static class StreamBuffer
    {
        public static TimeSpan TimeBufferDepthDefault { get; set; } = TimeSpan.FromSeconds(10);
    }

    public static class DashClient
    {
        public static TimeSpan TimeBufferDepthDefault { get; set; } = TimeSpan.FromSeconds(10);
        public static double MinimumSegmentFitRatio { get; set; } = 0.7;
        public static TimeSpan MinimumBufferTime { get; set; } = TimeSpan.FromSeconds(3);
        public static TimeSpan DynamicSegmentAvailabilityOverhead = TimeSpan.FromSeconds(2);
    }

    public static class DashDownloader
    {
        public static int ChunkSize { get; set; } = 64 * 1024;
    }

    public static class DashManifest
    {
        public static TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromSeconds(3);
        public static int MaxManifestDownloadRetries { get; set; } = 3;
        public static TimeSpan ManifestDownloadDelay { get; set; } = TimeSpan.FromMilliseconds(1000);
        public static TimeSpan ManifestReloadDelay { get; set; } = TimeSpan.FromMilliseconds(1500);
    }

    public static class DashMediaPipeline
    {
        public static TimeSpan SegmentEps { get; set; } = TimeSpan.FromSeconds(0.5);
    }

    public static class HLSDataProvider
    {
        public static TimeSpan MaxBufferHealth { get; set; } = TimeSpan.FromSeconds(10);
    }

    public static class RTSPDataProvider
    {
        public static TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(2);
    }

    public static class EWMAThroughputHistory
    {
        public static double SlowEWMACoeff { get; set; } = 0.99;
        public static double FastEWMACoeff { get; set; } = 0.98;
        public static double SlowBandwidth { get; set; } = 20000000;
        public static double FastBandwidth { get; set; } = 20000000;
    }

    public static class ThroughputHistory
    {
        public static int MaxMeasurementsToKeep { get; set; } = 20;
        public static int AverageThroughputSampleAmount { get; set; } = 4;
        public static int MinimumThroughputSampleAmount { get; set; } = 2;

        public static double ThroughputDecreaseScale { get; set; } = 1.3;
        public static double ThroughputIncreaseScale { get; set; } = 1.3;
    }

    public static class FFmpegDemuxer
    {
        public static ulong BufferSize { get; set; } = 64 * 1024; // 32kB seems to be "low level standard", but content downloading pipeline works better for 64kB

        public static int ProbeSize { get; set; } = 32 * 1024; // higher values may cause problems when probing certain kinds of content (assert "len >= s->orig_buffer_size" in aviobuf)

        public static TimeSpan MaxAnalyzeDuration { get; set; } = TimeSpan.FromSeconds(10);
    }

    public static class CencSession
    {
        public static int MaxDecryptRetries { get; set; } = 5;
        public static TimeSpan DecryptBufferFullSleepTime { get; set; } = TimeSpan.FromMilliseconds(1000);
    }

    public static class EsStream
    {
        public static TimeSpan TransferChunk { get; set; } = TimeSpan.FromSeconds(2);
    }
}
