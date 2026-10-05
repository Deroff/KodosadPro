namespace KodosadStudio;

public enum DraftSaveStart
{
    Clean,
    Busy,
    Started
}

/// <summary>Tracks dirty revisions and ensures only one draft write is active at a time.</summary>
public sealed class DraftAutosaveCoordinator
{
    private readonly object _sync = new();
    private long _revision;
    private long _savedRevision;
    private long _activeRevision;
    private bool _isSaving;

    public long Revision
    {
        get { lock (_sync) return _revision; }
    }

    public void MarkChanged()
    {
        lock (_sync) _revision++;
    }

    public DraftSaveStart TryBegin(out long revision)
    {
        lock (_sync)
        {
            revision = _revision;
            if (_isSaving) return DraftSaveStart.Busy;
            if (_savedRevision == _revision) return DraftSaveStart.Clean;
            _isSaving = true;
            _activeRevision = _revision;
            revision = _activeRevision;
            return DraftSaveStart.Started;
        }
    }

    /// <returns>True when a newer or failed revision remains unsaved.</returns>
    public bool Complete(long revision, bool succeeded)
    {
        lock (_sync)
        {
            if (!_isSaving || revision != _activeRevision)
                throw new InvalidOperationException("Завершена неизвестная попытка автосохранения.");
            _isSaving = false;
            if (succeeded) _savedRevision = revision;
            return _savedRevision < _revision;
        }
    }
}
