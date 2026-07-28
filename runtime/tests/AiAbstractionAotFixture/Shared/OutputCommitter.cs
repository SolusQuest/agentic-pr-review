namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal static class OutputCommitter
{
    internal static void CommitFirst(
        ProofState state,
        FirstEvidence evidence,
        string statePath,
        string evidencePath,
        string? injectedFault = null)
    {
        EnsureAbsent(statePath);
        EnsureAbsent(evidencePath);
        var stateBytes = AddLineFeed(FixtureJson.Serialize(state));
        var evidenceBytes = AddLineFeed(FixtureJson.Serialize(evidence));
        _ = FixtureJson.Deserialize(stateBytes, FixtureJsonContext.Default.ProofState);
        _ = FixtureJson.Deserialize(evidenceBytes, FixtureJsonContext.Default.FirstEvidence);

        var stateStage = Stage(statePath, stateBytes);
        var evidenceStage = Stage(evidencePath, evidenceBytes);
        var evidenceCommitted = false;
        try
        {
            if (injectedFault == "before-evidence-commit")
            {
                throw new FixtureFailure("APR_AI_EVIDENCE");
            }
            File.Move(evidenceStage, evidencePath, overwrite: false);
            evidenceCommitted = true;
            if (injectedFault == "before-state-commit")
            {
                throw new FixtureFailure("APR_AI_STATE_COMMIT");
            }
            File.Move(stateStage, statePath, overwrite: false);
        }
        catch
        {
            TryDelete(stateStage);
            TryDelete(evidenceStage);
            if (evidenceCommitted && !File.Exists(statePath))
            {
                TryDelete(evidencePath);
            }
            throw;
        }
    }

    internal static void CommitResume(
        ResumeEvidence evidence,
        CombinedEvidence combined,
        string evidencePath,
        string combinedPath)
    {
        EnsureAbsent(evidencePath);
        EnsureAbsent(combinedPath);
        var evidenceBytes = AddLineFeed(FixtureJson.Serialize(evidence));
        var combinedBytes = AddLineFeed(FixtureJson.Serialize(combined));
        _ = FixtureJson.Deserialize(evidenceBytes, FixtureJsonContext.Default.ResumeEvidence);
        _ = FixtureJson.Deserialize(combinedBytes, FixtureJsonContext.Default.CombinedEvidence);
        var combinedStage = Stage(combinedPath, combinedBytes);
        var evidenceStage = Stage(evidencePath, evidenceBytes);
        try
        {
            File.Move(evidenceStage, evidencePath, overwrite: false);
            File.Move(combinedStage, combinedPath, overwrite: false);
        }
        catch
        {
            TryDelete(evidenceStage);
            TryDelete(combinedStage);
            TryDelete(evidencePath);
            TryDelete(combinedPath);
            throw;
        }
    }

    private static string Stage(string destination, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(directory);
        var fileName = Path.GetFileName(destination);
        if (string.IsNullOrEmpty(fileName))
        {
            throw new FixtureFailure("APR_AI_OUTPUT_PATH");
        }
        var stage = Path.Join(
            directory,
            $".{fileName}.{Guid.NewGuid():N}.stage");
        using var stream = new FileStream(
            stage,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return stage;
    }

    private static void EnsureAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new FixtureFailure("APR_AI_OUTPUT_EXISTS");
        }
    }

    private static byte[] AddLineFeed(byte[] bytes)
    {
        var output = new byte[bytes.Length + 1];
        bytes.CopyTo(output, 0);
        output[^1] = (byte)'\n';
        return output;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort only. Staging paths are never accepted by the restore path.
        }
    }
}
