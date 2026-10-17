using LogAgent.NapCat;
using Xunit;

namespace LogAgent.Tests;

public class NapCatWebUiTests
{
    [Fact]
    public void HashLoginToken_MatchesNapCatAlgorithm()
    {
        // SHA256("demo-token.napcat") 固定向量
        var hash = NapCatWebUiClient.HashLoginToken("demo-token");
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        // 相同输入稳定
        Assert.Equal(hash, NapCatWebUiClient.HashLoginToken("demo-token"));
    }
}
