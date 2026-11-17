using System.Text;
using PrimSystem.Services;
using Xunit;

namespace PrimSystem.Tests
{
    public class TempCredentialTests
    {
        private static string Oku(byte[] bytes) => new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');

        [Fact]
        public void Csv_Basliklari_Ve_Satirlari_Icerir_Ve_BOM_ile_Baslar()
        {
            var bytes = TempCredential.ToCsv(new[] { new TempCredential(1, "Ayşe Yılmaz", "AyşeYı", "Ab3dEf5hJk") });
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            Assert.Equal("Ad Soyad;Kullanıcı Adı;Geçici Şifre\r\nAyşe Yılmaz;AyşeYı;Ab3dEf5hJk\r\n", Oku(bytes));
        }

        [Theory]
        [InlineData("=1+1", "'=1+1")]
        [InlineData("+90", "'+90")]
        [InlineData("-cmd", "'-cmd")]
        [InlineData("@SUM(A1)", "'@SUM(A1)")]
        public void Csv_Formul_Baslangicli_Hucreyi_Etkisizlestirir(string girdi, string beklenen)
        {
            var csv = Oku(TempCredential.ToCsv(new[] { new TempCredential(1, girdi, "u", "p") }));
            Assert.Contains("\r\n" + beklenen + ";u;p\r\n", csv);
        }

        [Fact]
        public void Csv_Ayirici_Ve_Tirnak_Iceren_Hucreyi_Kacirir()
        {
            var csv = Oku(TempCredential.ToCsv(new[] { new TempCredential(1, "A;\"B\"", "u", "p") }));
            Assert.Contains("\"A;\"\"B\"\"\";u;p", csv);
        }
    }
}
