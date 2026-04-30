public sealed class CliOutputPathTests
{
    [Fact]
    public void GetPolicyExtension_Returns_Tf_For_Terraform()
    {
        var extension = CliOutputPath.GetPolicyExtension("terraform");
        Assert.Equal("tf", extension);
    }

    [Fact]
    public void GetPolicyExtension_Returns_Json_For_IamJson()
    {
        var extension = CliOutputPath.GetPolicyExtension("iam-json");
        Assert.Equal("json", extension);
    }
}
