using System.Net;
using ArchiTech.Api.Integrations;
using ArchiTech.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace ArchiTech.Tests;

public sealed class LocalEvidenceWorkerTests
{
    [Theory]
    [InlineData("ARCHITECH-EVIDENCE-ERROR:authentication-methods:http:403", "Graph authentication methods policy read failed (HTTP 403). No evidence was returned.")]
    [InlineData("warning\nARCHITECH-EVIDENCE-ERROR:authentication-methods:invalid-response\n", "Graph authentication methods policy response was incomplete or invalid. No evidence was returned.")]
    [InlineData("ARCHITECH-EVIDENCE-ERROR:authentication-methods:missing-migration-state", "Graph authentication methods policy did not include a migration state. No evidence was returned.")]
    public void Worker_errors_reveal_only_a_recognized_operation_and_status(string stderr, string expected)
    {
        Assert.Equal(expected, LocalEvidenceWorker.DescribeWorkerFailure(stderr));
    }

    [Theory]
    [InlineData("ARCHITECH-EVIDENCE-ERROR:authentication-methods:http:403 secret-token")]
    [InlineData("ARCHITECH-EVIDENCE-ERROR:secret-token:http:403")]
    [InlineData("ARCHITECH-EVIDENCE-ERROR:authentication-methods:http:403\nARCHITECH-EVIDENCE-ERROR:policies:http:500")]
    public void Unexpected_worker_errors_do_not_leak_details(string stderr)
    {
        var result = LocalEvidenceWorker.DescribeWorkerFailure(stderr);
        Assert.Contains("worker failed", result);
        Assert.DoesNotContain("secret-token", result);
    }

    [Theory]
    [InlineData("Development", true, "127.0.0.1", "127.0.0.1:5080", "http://127.0.0.1:5080", true)]
    [InlineData("Development", true, "::1", "localhost:5080", "http://localhost:5173", true)]
    [InlineData("Production", true, "127.0.0.1", "localhost:5080", "http://localhost:5080", false)]
    [InlineData("Development", false, "127.0.0.1", "localhost:5080", "http://localhost:5080", false)]
    [InlineData("Development", true, "192.168.1.1", "localhost:5080", "http://localhost:5080", false)]
    [InlineData("Development", true, "127.0.0.1", "evil.example:5080", "http://localhost:5080", false)]
    [InlineData("Development", true, "127.0.0.1", "localhost:5080", "https://evil.example:5080", false)]
    [InlineData("Development", true, "127.0.0.1", "localhost:5080", "http://localhost:9999", false)]
    [InlineData("Development", true, "127.0.0.1", "localhost:5080", "", false)]
    public void Live_reads_require_local_development_and_approved_origin(
        string environment, bool localAuth, string address, string host, string origin, bool expected)
    {
        var worker = new LocalEvidenceWorker(new TestEnvironment { EnvironmentName = environment }, new AuthenticationMode(localAuth));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Request.Host = HostString.FromUriComponent(host);
        context.Request.Headers.Origin = origin;
        Assert.Equal(expected, worker.IsAvailable(context));
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "";
        public string ApplicationName { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
