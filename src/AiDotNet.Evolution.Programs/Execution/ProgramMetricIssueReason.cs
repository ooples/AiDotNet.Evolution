// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Enums/ProgramMetricIssueReason.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.Globalization;
namespace AiDotNet.Evolution.Programs;

/// <summary>Standalone ProgramMetricIssueReason.</summary>
public enum ProgramMetricIssueReason
{
    /// <summary>A text metric was skipped because it could not be read as a finite number.</summary>
    NonNumericText = 0,

    /// <summary>A boolean flag was skipped because a flag is not a score.</summary>
    BooleanFlag = 1,

    /// <summary>A numeric metric was skipped because its value was not a number.</summary>
    NotANumber = 2,

    /// <summary>A numeric metric was infinite, so the aggregated value cannot be finite.</summary>
    NotFinite = 3,

    /// <summary>A metric named by the configuration was absent from the reported dictionary.</summary>
    MissingMetric = 4,

    /// <summary>A metric was excluded from the aggregation because it is an archive feature dimension.</summary>
    ExcludedFeatureDimension = 5,

    /// <summary>A numeric metric was reported but the weighted strategy declares no weight for it.</summary>
    NoWeightDeclared = 6,

    /// <summary>No usable numeric metric was found at all, so the aggregation fell back to zero.</summary>
    NoNumericValues = 7
}
