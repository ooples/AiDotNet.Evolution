// Migrated from ooples/AiDotNet 66d7602c92101e5ab2bd9db8cfa7f7526fa2c75d:src/Evolution/Programs/ICostedProgramProposalSource.cs
// Original license retained in AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs;

/// <summary>A program proposal backend that receipts all of its work and exposes cumulative model usage.</summary>
/// <remarks>Include mutation/refinement, model requests, parsing, compilation, repairs and audit writes performed
/// inside the backend in its returned resources. Do not charge those same operations separately. Evaluator and
/// one-time setup costs remain separate ledger operations. Failed or unknown work must not become a zero charge.</remarks>
public interface ICostedProgramProposalSource : ICostedEvolutionProposalSource<ProgramGenome>
{
    /// <summary>Returns cumulative provider-reported usage, including failed requests and repairs.</summary>
    ProgramEvolutionLlmUsage GetUsage();
}
