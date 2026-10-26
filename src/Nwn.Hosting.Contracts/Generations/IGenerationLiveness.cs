namespace Nwn.Hosting.Contracts.Generations;

public interface IGenerationLiveness
{
    long Generation { get; }
    bool IsCurrent(long generation);
}
