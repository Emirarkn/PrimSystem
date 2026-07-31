using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests
{
    public class LoginThrottleTests
    {
        [Fact]
        public void BesDenemedenSonra_Kilitlenir()
        {
            var t = new LoginThrottle();
            for (int i = 0; i < 5; i++)
            {
                Assert.Null(t.GetLockRemaining("emir")); // kilit ancak 5. denemeden sonra
                t.RegisterFailure("emir");
            }
            var remaining = t.GetLockRemaining("emir");
            Assert.NotNull(remaining);
            Assert.True(remaining > TimeSpan.FromMinutes(14)); // ~15 dk kilit
        }

        [Fact]
        public void DortDeneme_KilitlemeZ()
        {
            var t = new LoginThrottle();
            for (int i = 0; i < 4; i++) t.RegisterFailure("emir");
            Assert.Null(t.GetLockRemaining("emir"));
        }

        [Fact]
        public void Reset_KilidiKaldirir()
        {
            var t = new LoginThrottle();
            for (int i = 0; i < 5; i++) t.RegisterFailure("emir");
            Assert.NotNull(t.GetLockRemaining("emir"));
            t.Reset("emir");
            Assert.Null(t.GetLockRemaining("emir"));
        }

        [Fact]
        public void KullaniciAdi_BuyukKucukDuyarsiz()
        {
            var t = new LoginThrottle();
            for (int i = 0; i < 5; i++) t.RegisterFailure("Emir ");
            Assert.NotNull(t.GetLockRemaining("emir")); // trim + lowercase aynı anahtara düşer
        }

        [Fact]
        public void FarkliKullanicilar_BirbirindenBagimsiz()
        {
            var t = new LoginThrottle();
            for (int i = 0; i < 5; i++) t.RegisterFailure("ahmet");
            Assert.NotNull(t.GetLockRemaining("ahmet"));
            Assert.Null(t.GetLockRemaining("mehmet"));
        }
    }

    public class PasswordPolicyTests
    {
        [Theory]
        [InlineData("Abcdef12")]      // tam sınırda: 8 karakter, harf + rakam
        [InlineData("uzunParola123")]
        [InlineData("X1234567")]
        public void GecerliParolalar_HataAtmaz(string pwd)
        {
            AuthService.ValidatePasswordStrength(pwd); // exception yoksa geçer
        }

        [Theory]
        [InlineData("")]            // boş
        [InlineData("   ")]         // whitespace
        [InlineData("Abc12")]       // 8'den kısa
        [InlineData("12345678")]    // harf yok
        [InlineData("abcdefgh")]    // rakam yok
        public void GecersizParolalar_ArgumentException(string pwd)
        {
            Assert.Throws<ArgumentException>(() => AuthService.ValidatePasswordStrength(pwd));
        }

        [Fact]
        public void UretilenVarsayilanParola_PolitikayaUyar()
        {
            // GenerateDefaultPassword'ün ürettiği parolalar kendi politikamızı geçmeli
            for (int i = 0; i < 20; i++)
            {
                var pwd = AuthService.GenerateDefaultPassword("Test Kullanıcı");
                Assert.Equal(10, pwd.Length);
                AuthService.ValidatePasswordStrength(pwd);
            }
        }
    }
}
