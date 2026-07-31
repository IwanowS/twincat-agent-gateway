using System;
using System.Collections.Generic;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

internal sealed class ResolvedXaeBuildTarget
{
    public ResolvedXaeBuildTarget(
        XaeBuildScope scope,
        string? project,
        string? projectFile)
    {
        Scope = scope;
        Project = project;
        ProjectFile = projectFile;
    }

    public XaeBuildScope Scope { get; }

    public string? Project { get; }

    public string? ProjectFile { get; }
}

internal static class XaeBuildTargetResolver
{
    private const string Stage = "xae.build.project.resolve";

    public static ResolvedXaeBuildTarget Resolve(
        TwinCatProjectGraphSnapshot graph,
        XaeBuildScope scope,
        string? requestedProject)
    {
        if (graph is null)
        {
            throw new ArgumentNullException(nameof(graph));
        }

        if (!Enum.IsDefined(typeof(XaeBuildScope), scope))
        {
            throw RequestInvalid("Build scope is not supported.");
        }

        string? normalizedProject = string.IsNullOrWhiteSpace(
            requestedProject)
            ? null
            : requestedProject.Trim();
        if (scope == XaeBuildScope.Solution)
        {
            if (normalizedProject is not null)
            {
                throw RequestInvalid(
                    "A logical project cannot be supplied for solution "
                    + "scope.");
            }

            return new ResolvedXaeBuildTarget(
                XaeBuildScope.Solution,
                project: null,
                projectFile: null);
        }

        if (!graph.IsComplete)
        {
            throw new GatewayOperationException(
                ErrorCodes.ProjectGraphInvalid,
                "The TwinCAT project graph is incomplete and cannot "
                    + "select a PLC project for build.",
                retryable: true,
                stage: Stage,
                component: GatewayComponent.Xae);
        }

        ResolvedXaeBuildTarget[] projects = graph.Entries
            .Where(entry =>
                entry.Role == ProjectGraphFileRole.PlcProject)
            .GroupBy(
                entry => string.Join(
                    "\0",
                    entry.Project,
                    entry.ProjectFile),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(
                entry => entry.Project,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                entry => entry.ProjectFile,
                StringComparer.OrdinalIgnoreCase)
            .Select(entry => new ResolvedXaeBuildTarget(
                XaeBuildScope.Plc,
                entry.Project,
                entry.ProjectFile))
            .ToArray();
        if (normalizedProject is null)
        {
            return projects.Length switch
            {
                1 => projects[0],
                0 => throw ProjectNotFound(
                    "The selected TwinCAT graph contains no PLC project."),
                _ => throw ProjectAmbiguous(
                    "The selected TwinCAT graph contains multiple PLC "
                    + "projects; specify one logical project id."),
            };
        }

        ResolvedXaeBuildTarget[] matches = projects
            .Where(project => string.Equals(
                project.Project,
                normalizedProject,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw ProjectNotFound(
                $"PLC project '{normalizedProject}' was not found in "
                + "the selected TwinCAT graph."),
            _ => throw ProjectAmbiguous(
                $"Logical PLC project id '{normalizedProject}' matches "
                + "multiple project files."),
        };
    }

    private static GatewayOperationException RequestInvalid(
        string message)
    {
        return new GatewayOperationException(
            ErrorCodes.RequestInvalid,
            message,
            stage: Stage,
            component: GatewayComponent.Xae);
    }

    private static GatewayOperationException ProjectNotFound(
        string message)
    {
        return new GatewayOperationException(
            ErrorCodes.BuildProjectNotFound,
            message,
            stage: Stage,
            component: GatewayComponent.Xae);
    }

    private static GatewayOperationException ProjectAmbiguous(
        string message)
    {
        return new GatewayOperationException(
            ErrorCodes.BuildProjectAmbiguous,
            message,
            stage: Stage,
            component: GatewayComponent.Xae);
    }
}
