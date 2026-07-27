using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class TcUnitReportParseResult
{
    public TestCounts Counts { get; set; } = new();

    public IReadOnlyList<TestFailure> Failures { get; set; } =
        Array.Empty<TestFailure>();

    public int MoreFailures { get; set; }
}

public static class TcUnitReportParser
{
    public const int DefaultMaximumFailures = 20;

    public static TcUnitReportParseResult Parse(
        string xml,
        int maximumFailures = DefaultMaximumFailures)
    {
        if (xml is null)
        {
            throw new ArgumentNullException(nameof(xml));
        }

        if (maximumFailures <= 0
            || maximumFailures > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFailures));
        }

        XDocument document;
        try
        {
            using StringReader text = new(xml);
            using XmlReader reader = XmlReader.Create(
                text,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                });
            document = XDocument.Load(
                reader,
                LoadOptions.None);
        }
        catch (Exception exception) when (
            exception is XmlException
            || exception is InvalidOperationException)
        {
            throw InvalidReport(
                "TcUnit report is not well-formed XML.",
                exception);
        }

        XElement root = document.Root
            ?? throw InvalidReport(
                "TcUnit report has no document element.");
        if (!HasLocalName(root, "testsuites")
            && !HasLocalName(root, "testsuite"))
        {
            throw InvalidReport(
                "TcUnit report root must be testsuites or testsuite.");
        }

        List<XElement> suites = root
            .DescendantsAndSelf()
            .Where(element =>
                HasLocalName(element, "testsuite"))
            .ToList();
        if (suites.Count == 0)
        {
            throw InvalidReport(
                "TcUnit report contains no test suite.");
        }

        List<XElement> cases = suites
            .SelectMany(suite =>
                suite.Elements().Where(element =>
                    HasLocalName(element, "testcase")))
            .ToList();
        List<TestFailure> allFailures = new();
        int skipped = 0;
        foreach (XElement testCase in cases)
        {
            string name = RequiredAttribute(
                testCase,
                "name",
                "A test case has no name.");
            XElement? failure = testCase
                .Elements()
                .FirstOrDefault(element =>
                    HasLocalName(element, "failure")
                    || HasLocalName(element, "error"));
            if (failure is not null)
            {
                allFailures.Add(
                    CreateFailure(
                        testCase,
                        failure,
                        name));
                continue;
            }

            if (testCase.Elements().Any(
                element =>
                    HasLocalName(element, "skipped")))
            {
                skipped++;
            }
        }

        TestCounts counts = new()
        {
            Suites = suites.Count,
            Tests = cases.Count,
            Failed = allFailures.Count,
            Skipped = skipped,
            Passed =
                cases.Count
                - allFailures.Count
                - skipped,
        };
        return new TcUnitReportParseResult
        {
            Counts = counts,
            Failures = allFailures
                .Take(maximumFailures)
                .ToList(),
            MoreFailures = Math.Max(
                0,
                allFailures.Count - maximumFailures),
        };
    }

    private static TestFailure CreateFailure(
        XElement testCase,
        XElement failure,
        string testName)
    {
        XElement suite = testCase.Ancestors()
            .First(element =>
                HasLocalName(element, "testsuite"));
        string suiteName =
            OptionalAttribute(testCase, "classname")
            ?? OptionalAttribute(suite, "name")
            ?? string.Empty;
        string message =
            OptionalAttribute(failure, "message")
            ?? NormalizeText(failure.Value)
            ?? "Test failed.";
        int? line = null;
        string? lineValue =
            OptionalAttribute(testCase, "line")
            ?? OptionalAttribute(failure, "line");
        if (lineValue is not null
            && int.TryParse(
                lineValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsedLine)
            && parsedLine > 0)
        {
            line = parsedLine;
        }

        return new TestFailure
        {
            Suite = suiteName,
            Name = testName,
            Message = message,
            File =
                OptionalAttribute(testCase, "file")
                ?? OptionalAttribute(failure, "file"),
            Line = line,
        };
    }

    private static string RequiredAttribute(
        XElement element,
        string name,
        string error)
    {
        return OptionalAttribute(element, name)
            ?? throw InvalidReport(error);
    }

    private static string? OptionalAttribute(
        XElement element,
        string name)
    {
        return NormalizeText(
            element.Attribute(name)?.Value);
    }

    private static string? NormalizeText(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value!.Trim();
    }

    private static bool HasLocalName(
        XElement element,
        string name)
    {
        return string.Equals(
            element.Name.LocalName,
            name,
            StringComparison.Ordinal);
    }

    private static GatewayOperationException InvalidReport(
        string message,
        Exception? innerException = null)
    {
        return new GatewayOperationException(
            ErrorCodes.TestReportInvalid,
            message,
            retryable: false,
            stage: "tcunit.report",
            innerException: innerException);
    }
}
