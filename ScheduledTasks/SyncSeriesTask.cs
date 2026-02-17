using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato;
using Gelato.Common;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Tasks {
    public sealed class SyncRunningSeriesTask : IScheduledTask {
        private readonly ILibraryManager _library;
        private readonly ILogger<SyncRunningSeriesTask> _log;
        private readonly IFileSystem _fs;
        private readonly GelatoManager _manager;

        public SyncRunningSeriesTask(
            ILibraryManager library,
            ILogger<SyncRunningSeriesTask> log,
            IFileSystem fs,
            GelatoManager manager
        ) {
            _library = library;
            _log = log;
            _fs = fs;
            _manager = manager;
        }

        public string Name => "Fetch missing season/episodes";
        public string Key => "SyncRunningSeries";
        public string Description =>
            "Scans all TV libraries for continuing series and builds their series trees.";
        public string Category => "Gelato";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(24).Ticks,
                },
            };
        }

        public async Task ExecuteAsync(
            IProgress<double> progress,
            CancellationToken cancellationToken
        ) {
            await _manager.SyncSeries(true, Guid.Empty, progress, cancellationToken);
        }
    }
}
