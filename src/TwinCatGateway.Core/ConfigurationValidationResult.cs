using System;
using System.Collections.Generic;
using System.Linq;

namespace TwinCatGateway.Core;

public sealed class ConfigurationValidationResult
{
    internal ConfigurationValidationResult(IEnumerable<ConfigurationIssue> issues)
    {
        Issues = issues?.ToArray()
            ?? throw new ArgumentNullException(nameof(issues));
    }

    public bool IsValid => Issues.Count == 0;

    public IReadOnlyList<ConfigurationIssue> Issues { get; }
}
