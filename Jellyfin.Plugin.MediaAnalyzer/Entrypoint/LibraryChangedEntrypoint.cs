using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaAnalyzer;

/// <summary>
/// Act on changes of the jellyfin library.
/// </summary>
public class LibraryChangedEntrypoint : IServerEntryPoint
{
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<LibraryChangedEntrypoint> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private Timer _queueTimer;
    private bool _analyzeAgain;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangedEntrypoint"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public LibraryChangedEntrypoint(
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILogger<LibraryChangedEntrypoint> logger,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;
        _loggerFactory = loggerFactory;

        _queueTimer = new Timer(
                OnQueueTimerCallback,
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Run observer tasks for observed events.
    /// </summary>
    /// <returns>Task.</returns>
    public Task RunAsync()
    {
        _libraryManager.ItemAdded += LibraryManagerItemAdded;
        _libraryManager.ItemUpdated += LibraryManagerItemUpdated;
        _libraryManager.ItemRemoved += LibraryManagerItemRemoved;
        _taskManager.TaskCompleted += TaskManagerTaskCompleted;
        FFmpegWrapper.Logger = _logger;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Delete blacklisted segments for itemid when library removed it.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private void LibraryManagerItemRemoved(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        if (itemChangeEventArgs.Item is not Movie and not Episode)
        {
            return;
        }

        if (itemChangeEventArgs.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        Plugin.Instance!.DeleteBlacklist(itemChangeEventArgs.Item.Id);
    }

    /// <summary>
    /// Library item was added.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private void LibraryManagerItemAdded(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        if (!Plugin.Instance!.Configuration.RunAfterAddOrUpdateEvent)
        {
            return;
        }

        // Don't do anything if it's not a supported media type
        if (itemChangeEventArgs.Item is not Movie and not Episode)
        {
            return;
        }

        if (itemChangeEventArgs.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        StartTimer();
    }

    /// <summary>
    /// TaskManager task ended.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="eventArgs">The <see cref="TaskCompletionEventArgs"/>.</param>
    private void TaskManagerTaskCompleted(object? sender, TaskCompletionEventArgs eventArgs)
    {
        var result = eventArgs.Result;

        if (!Plugin.Instance!.Configuration.RunAfterLibraryScan)
        {
            return;
        }

        if (result.Key != "RefreshLibrary")
        {
            return;
        }

        if (result.Status != TaskCompletionStatus.Completed)
        {
            return;
        }

        StartTimer();
    }

    /// <summary>
    /// Library item was updated.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private void LibraryManagerItemUpdated(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        if (!Plugin.Instance!.Configuration.RunAfterAddOrUpdateEvent)
        {
            return;
        }

        // Don't do anything if it's not a supported media type
        if (itemChangeEventArgs.Item is not Movie and not Episode)
        {
            return;
        }

        if (itemChangeEventArgs.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        StartTimer();
    }

    /// <summary>
    /// Start or restart timer to debounce analyzing.
    /// </summary>
    private void StartTimer()
    {
        if (Plugin.Instance!.AnalysisRunning)
        {
            _analyzeAgain = true;
        }
        else
        {
            _logger.LogInformation("Media Library changed, analyzis will start soon!");
            _queueTimer.Change(TimeSpan.FromMilliseconds(15000), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Wait for timer callback to be completed.
    /// </summary>
    private void OnQueueTimerCallback(object? state)
    {
        try
        {
            OnQueueTimerCallbackInternal();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnQueueTimerCallbackInternal");
        }
    }

    /// <summary>
    /// Wait for timer to be completed.
    /// </summary>
    private void OnQueueTimerCallbackInternal()
    {
        _logger.LogInformation("Timer elapsed - start analyzing");
        Plugin.Instance!.AnalysisRunning = true;
        var progress = new Progress<double>();
        var cancellationToken = new CancellationToken(false);

        // load blacklist
        Plugin.Instance!.GetBlacklistFromDb();

        // intro
        var introBaseAnalyzer = new BaseItemAnalyzerTask(
            AnalysisMode.Introduction,
            _loggerFactory.CreateLogger<AnalyzeMedia>(),
            _loggerFactory,
            _libraryManager);

        introBaseAnalyzer.AnalyzeItems(progress, cancellationToken);

        // outro
        var outroBaseAnalyzer = new BaseItemAnalyzerTask(
            AnalysisMode.Credits,
            _loggerFactory.CreateLogger<AnalyzeMedia>(),
            _loggerFactory,
            _libraryManager);

        outroBaseAnalyzer.AnalyzeItems(progress, cancellationToken);

        // save blacklist to db
        Plugin.Instance!.SaveBlacklist();

        // reset blacklist
        Plugin.Instance!.Blacklist.Clear();

        Plugin.Instance!.AnalysisRunning = false;

        // we might need to analyze again
        if (_analyzeAgain)
        {
            _logger.LogInformation("Analyzing ended, but we need to analyze again!");
            _analyzeAgain = false;
            StartTimer();
        }
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Protected dispose.
    /// </summary>
    /// <param name="dispose">Dispose.</param>
    protected virtual void Dispose(bool dispose)
    {
        if (!dispose)
        {
            _libraryManager.ItemAdded -= LibraryManagerItemAdded;
            _libraryManager.ItemUpdated -= LibraryManagerItemUpdated;
            _libraryManager.ItemRemoved -= LibraryManagerItemRemoved;

            _taskManager.TaskCompleted -= TaskManagerTaskCompleted;

            _queueTimer.Dispose();

            return;
        }
    }
}
