public sealed class CliArgsTests
{
    [Fact]
    public void Parse_Accepts_Terraform_Format()
    {
        var parsed = CliArgs.Parse(
        [
            "generate",
            "--repo", "/tmp/repo",
            "--out", "/tmp/out.tf",
            "--format", "terraform"
        ]);

        Assert.True(parsed.IsValid);
        Assert.Equal("terraform", parsed.Format);
    }

    [Fact]
    public void Parse_Rejects_Unsupported_Format()
    {
        var parsed = CliArgs.Parse(
        [
            "generate",
            "--repo", "/tmp/repo",
            "--out", "/tmp/out.tf",
            "--format", "xml"
        ]);

        Assert.False(parsed.IsValid);
        Assert.Equal("--format must be iam-json or terraform", parsed.ErrorMessage);
    }
}
