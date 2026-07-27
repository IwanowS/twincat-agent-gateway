using System.Linq;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class TcUnitReportParserTests
{
    [Fact]
    public void ParsesCompactCountsAndFailures()
    {
        const string xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <testsuites tests="4" failures="1" skipped="1">
              <testsuite name="MotionTests" tests="3" failures="1" skipped="1">
                <testcase name="Starts" classname="MotionTests" />
                <testcase name="Stops" classname="MotionTests" file="MotionTests.TcPOU" line="42">
                  <failure message="Expected stop">details</failure>
                </testcase>
                <testcase name="Optional" classname="MotionTests">
                  <skipped />
                </testcase>
              </testsuite>
              <testsuite name="IoTests" tests="1">
                <testcase name="Maps" />
              </testsuite>
            </testsuites>
            """;

        TcUnitReportParseResult result =
            TcUnitReportParser.Parse(xml);

        Assert.Equal(2, result.Counts.Suites);
        Assert.Equal(4, result.Counts.Tests);
        Assert.Equal(2, result.Counts.Passed);
        Assert.Equal(1, result.Counts.Failed);
        Assert.Equal(1, result.Counts.Skipped);
        TestFailure failure =
            Assert.Single(result.Failures);
        Assert.Equal("MotionTests", failure.Suite);
        Assert.Equal("Stops", failure.Name);
        Assert.Equal(
            "Expected stop",
            failure.Message);
        Assert.Equal(
            "MotionTests.TcPOU",
            failure.File);
        Assert.Equal(42, failure.Line);
        Assert.Equal(0, result.MoreFailures);
    }

    [Fact]
    public void SupportsSingleSuiteRootAndErrorElement()
    {
        const string xml =
            """
            <testsuite name="Suite">
              <testcase name="Broken">
                <error>Runtime exception</error>
              </testcase>
            </testsuite>
            """;

        TcUnitReportParseResult result =
            TcUnitReportParser.Parse(xml);

        Assert.Equal(1, result.Counts.Suites);
        Assert.Equal(1, result.Counts.Failed);
        Assert.Equal(
            "Runtime exception",
            Assert.Single(result.Failures).Message);
    }

    [Fact]
    public void CapsCompactFailures()
    {
        string cases = string.Join(
            string.Empty,
            Enumerable.Range(1, 25).Select(
                number =>
                    $"<testcase name=\"T{number}\">"
                    + "<failure message=\"failed\" />"
                    + "</testcase>"));
        string xml =
            $"<testsuite name=\"Suite\">{cases}</testsuite>";

        TcUnitReportParseResult result =
            TcUnitReportParser.Parse(
                xml,
                maximumFailures: 20);

        Assert.Equal(25, result.Counts.Failed);
        Assert.Equal(20, result.Failures.Count);
        Assert.Equal(5, result.MoreFailures);
    }

    [Theory]
    [InlineData("<testsuites>")]
    [InlineData("<result />")]
    [InlineData("<testsuites />")]
    [InlineData(
        "<testsuite name=\"Suite\"><testcase /></testsuite>")]
    public void RejectsInvalidOrIncompleteReports(
        string xml)
    {
        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => TcUnitReportParser.Parse(xml));

        Assert.Equal(
            ErrorCodes.TestReportInvalid,
            exception.Code);
        Assert.Equal(
            "tcunit.report",
            exception.Stage);
    }

    [Fact]
    public void ZeroTestSuiteRemainsValidForPolicyLayer()
    {
        TcUnitReportParseResult result =
            TcUnitReportParser.Parse(
                "<testsuite name=\"Empty\" />");

        Assert.Equal(1, result.Counts.Suites);
        Assert.Equal(0, result.Counts.Tests);
        Assert.Empty(result.Failures);
    }
}
