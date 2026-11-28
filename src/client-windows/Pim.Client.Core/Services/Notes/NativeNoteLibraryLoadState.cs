namespace Pim.Client.Core.Services.Notes;

/// <summary>
/// Keeps the last successfully loaded library rows while a newer query is in
/// flight. Only the newest request ticket may replace rows or publish an error.
/// </summary>
public sealed class NativeNoteLibraryLoadState
{
    private readonly object _sync = new();
    private readonly List<LocalNoteSummary> _notes = [];
    private long _requestGeneration;
    private string _query = string.Empty;
    private bool _isLoading;
    private string? _error;

    public IReadOnlyList<LocalNoteSummary> Notes
    {
        get
        {
            lock (_sync)
            {
                return _notes.ToArray();
            }
        }
    }

    public string Query
    {
        get
        {
            lock (_sync)
            {
                return _query;
            }
        }
    }

    public bool IsLoading
    {
        get
        {
            lock (_sync)
            {
                return _isLoading;
            }
        }
    }

    public string? Error
    {
        get
        {
            lock (_sync)
            {
                return _error;
            }
        }
    }

    public long BeginRefresh(string? query)
    {
        lock (_sync)
        {
            _query = query ?? string.Empty;
            _isLoading = true;
            _error = null;
            return ++_requestGeneration;
        }
    }

    public bool IsCurrent(long requestGeneration)
    {
        lock (_sync)
        {
            return requestGeneration == _requestGeneration;
        }
    }

    public bool TryApply(long requestGeneration, IReadOnlyList<LocalNoteSummary> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        lock (_sync)
        {
            if (requestGeneration != _requestGeneration)
            {
                return false;
            }

            _notes.Clear();
            _notes.AddRange(notes);
            _isLoading = false;
            _error = null;
            return true;
        }
    }

    public bool TryFail(long requestGeneration, string? error)
    {
        lock (_sync)
        {
            if (requestGeneration != _requestGeneration)
            {
                return false;
            }

            _isLoading = false;
            _error = string.IsNullOrWhiteSpace(error) ? "读取本机笔记库失败。" : error;
            return true;
        }
    }

    public bool TryAbandon(long requestGeneration)
    {
        lock (_sync)
        {
            if (requestGeneration != _requestGeneration)
            {
                return false;
            }

            _isLoading = false;
            return true;
        }
    }

    public NativeNoteLibraryPresentation Presentation
    {
        get
        {
            lock (_sync)
            {
                return NativeNotePresentation.Library(
                    _isLoading,
                    _error is not null,
                    _notes.Count,
                    _query);
            }
        }
    }

    public void ClearError()
    {
        lock (_sync)
        {
            _error = null;
        }
    }
}
