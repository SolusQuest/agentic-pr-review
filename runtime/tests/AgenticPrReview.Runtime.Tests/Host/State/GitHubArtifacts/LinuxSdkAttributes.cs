namespace AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;

internal sealed class LinuxSdkFactAttribute : FactAttribute
{
    public LinuxSdkFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "The production SDK upload capability uses Linux held-directory traversal.";
    }
}

internal sealed class LinuxSdkTheoryAttribute : TheoryAttribute
{
    public LinuxSdkTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "The production SDK upload capability uses Linux held-directory traversal.";
    }
}
