using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests
{
    public class PasswordChangeGateTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("calculate")]
        [InlineData("/organization")]
        [InlineData("reports/")]
        public void Gecici_Sifreli_Kullanici_Diger_Sayfalardan_Sifre_Degistirmeye_Yonlenir(string yol)
        {
            Assert.Equal("/change-password", PasswordChangeGate.RedirectTarget(true, true, false, yol));
        }

        [Theory]
        [InlineData("change-password")]
        [InlineData("/Change-Password")]
        [InlineData("login")]
        [InlineData("forgot-password")]
        [InlineData("reset-password")]
        [InlineData("Error")]
        public void Muaf_Sayfalarda_Yonlendirme_Yok(string yol)
        {
            Assert.Null(PasswordChangeGate.RedirectTarget(true, true, false, yol));
        }

        [Fact]
        public void Bayrak_Kapaliysa_Yonlendirme_Yok()
        {
            Assert.Null(PasswordChangeGate.RedirectTarget(true, false, false, "calculate"));
        }

        [Fact]
        public void Giris_Yoksa_Yonlendirme_Yok()
        {
            Assert.Null(PasswordChangeGate.RedirectTarget(false, true, false, "calculate"));
        }

        [Fact]
        public void Taklit_Sirasinda_Yonlendirme_Yok()
        {
            Assert.Null(PasswordChangeGate.RedirectTarget(true, true, true, "calculate"));
        }
    }
}
