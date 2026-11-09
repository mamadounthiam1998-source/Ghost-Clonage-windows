using System.Diagnostics;

namespace Ghost.Core.Operations;

public enum OperationPhase
{
    Idle,
    Analysing,   // Analyse du disque
    Preparing,   // Préparation
    Working,     // Clonage / création d'image / restauration en cours
    Verifying,   // Vérification
    Finalizing,  // Finalisation
    Completed,   // Opération terminée
    Failed,      // Erreur
    Cancelled
}

public sealed record ProgressInfo(
    OperationPhase Phase,
    long BytesDone,
    long BytesTotal,
    double BytesPerSecond,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    string Message)
{
    public double Percent => BytesTotal <= 0 ? 0 : Math.Min(100.0, BytesDone * 100.0 / BytesTotal);
}

public enum OperationOutcome { Success, Failed, Cancelled }

public sealed record OperationResult(
    OperationOutcome Outcome,
    string Summary,
    IReadOnlyList<string> Warnings,
    long BytesProcessed,
    TimeSpan Duration,
    string? Sha256Hex = null)
{
    public bool IsSuccess => Outcome == OperationOutcome.Success;
}

/// <summary>Calcule pourcentage, vitesse (moyenne lissée), temps écoulé et restant.</summary>
public sealed class ProgressTracker
{
    private readonly IProgress<ProgressInfo>? _sink;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _totalWork;
    private long _lastBytes;
    private long _lastTicksMs;
    private double _ema;
    private long _lastEmit = -1000;
    private OperationPhase _phase = OperationPhase.Idle;
    private string _message = "";
    private long _done;

    public ProgressTracker(IProgress<ProgressInfo>? sink) => _sink = sink;

    public TimeSpan Elapsed => _sw.Elapsed;
    public long Done => _done;

    public void SetTotal(long total) => _totalWork = total;

    public void Phase(OperationPhase phase, string message)
    {
        _phase = phase;
        _message = message;
        Emit(true);
    }

    public void Report(long done, string? message = null)
    {
        _done = done;
        if (message != null) _message = message;

        long now = _sw.ElapsedMilliseconds;
        long dt = now - _lastTicksMs;
        if (dt >= 250)
        {
            double inst = (done - _lastBytes) * 1000.0 / dt;
            _ema = _ema <= 0 ? inst : _ema * 0.7 + inst * 0.3;
            _lastBytes = done;
            _lastTicksMs = now;
        }
        Emit(false);
    }

    private void Emit(bool force)
    {
        if (_sink == null) return;
        long now = _sw.ElapsedMilliseconds;
        if (!force && now - _lastEmit < 100) return;
        _lastEmit = now;

        TimeSpan? remaining = null;
        if (_ema > 1 && _totalWork > _done)
            remaining = TimeSpan.FromSeconds((_totalWork - _done) / _ema);

        _sink.Report(new ProgressInfo(_phase, _done, _totalWork, _ema, _sw.Elapsed, remaining, _message));
    }

    public void Final(OperationPhase phase, string message)
    {
        _phase = phase;
        _message = message;
        _lastEmit = -1000;
        if (phase == OperationPhase.Completed) _done = _totalWork;
        Emit(true);
    }
}
