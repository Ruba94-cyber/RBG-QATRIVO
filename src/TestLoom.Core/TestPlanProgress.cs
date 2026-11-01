namespace TestLoom.Core;

public sealed record TestPlanProgressReport(int Total, int Passed, int Failed, int Blocked, int NotRun, double CompletionPercent, double PassRatePercent)
{
    public string Summary => $"{Total} tests · {Passed} passed · {Failed} failed · {Blocked} blocked · {NotRun} not run · {CompletionPercent:0.#}% executed";
}

public static class TestPlanProgress
{
    public static TestPlanProgressReport Calculate(WorkspaceDocument workspace, WorkspaceTestPlan plan)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(plan);
        var members = workspace.TestCases.Where(t => !t.Archived && plan.TestIds.Contains(t.Id)).ToArray();
        int Count(ExecutionResult r) => members.Count(t => t.Executions.LastOrDefault()?.Result == r);
        var passed = Count(ExecutionResult.Passed); var failed = Count(ExecutionResult.Failed); var blocked = Count(ExecutionResult.Blocked);
        var executed = passed + failed + blocked;
        return new TestPlanProgressReport(members.Length, passed, failed, blocked, members.Length - executed,
            members.Length == 0 ? 0 : Math.Round(100.0 * executed / members.Length, 1),
            executed == 0 ? 0 : Math.Round(100.0 * passed / executed, 1));
    }

    public static bool SetMembership(WorkspaceDocument workspace, Guid planId, Guid testId, bool include)
    {
        var plan = workspace.Plans.FirstOrDefault(p => p.Id == planId) ?? throw new ArgumentException("Unknown plan.", nameof(planId));
        if (!workspace.TestCases.Any(t => t.Id == testId)) throw new ArgumentException("Unknown test.", nameof(testId));
        if (include) { if (plan.TestIds.Contains(testId)) return false; plan.TestIds.Add(testId); return true; }
        return plan.TestIds.Remove(testId);
    }
}
