namespace AiDotNet.Evolution;

public sealed partial class EvolutionEngine<TGenome>
{
    private void ValidateParetoPolicies()
    {
        bool pareto = _islands[0].GetParetoDefinition() is not null;
        if (!pareto)
        {
            if (_options.EarlyStopping.Metric == EvolutionEarlyStoppingMetric.ParetoHypervolume ||
                _selection is ParetoEvolutionSelectionPolicy<TGenome> || _migration is ParetoMigrationPolicy<TGenome>)
                throw new ArgumentException("Pareto policies require Pareto archives.");
            return;
        }
        if (_selection is not ParetoEvolutionSelectionPolicy<TGenome> || _migration is not ParetoMigrationPolicy<TGenome>)
            throw new ArgumentException("Pareto archives require explicit ParetoEvolutionSelectionPolicy and ParetoMigrationPolicy.");
        if (_options.TargetQuality.HasValue || _options.GlobalEliteCount != 0 || _options.HistorySize != 0 || _options.Cascade.Enabled)
            throw new ArgumentException("Pareto runs cannot use scalar targets, scalar elite/history pools or scalar cascade gates.");
        if (_options.EarlyStopping.PatienceEvaluations > 0 &&
            (_options.EarlyStopping.Metric != EvolutionEarlyStoppingMetric.ParetoHypervolume || _options.EarlyStopping.MetricName is not null))
            throw new ArgumentException("Pareto early stopping requires ParetoHypervolume without a scalar metric name.");
    }
}
