namespace Pim.Client.Core.Services.Notes;

public enum CloudNoteSourceAction
{
    Refresh,
    Restore,
    Select,
}

/// <summary>
/// Owns the single permitted server-source window action. The caller acquires
/// the action before awaiting the note coordinator so repeated clicks cannot
/// queue a second restore or selection.
/// </summary>
public sealed class CloudNoteSourceActionState
{
    private readonly object _sync = new();
    private CloudNoteSourceAction? _active;

    public CloudNoteSourceAction? Active
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    public bool IsBusy => Active is not null;

    public bool IsRestoring => Active == CloudNoteSourceAction.Restore;

    public bool TryBegin(CloudNoteSourceAction action)
    {
        lock (_sync)
        {
            if (_active is not null)
            {
                return false;
            }

            _active = action;
            return true;
        }
    }

    public bool Complete(CloudNoteSourceAction action)
    {
        lock (_sync)
        {
            if (_active != action)
            {
                return false;
            }

            _active = null;
            return true;
        }
    }
}
