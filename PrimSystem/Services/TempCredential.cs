using System.Text;

namespace PrimSystem.Services
{
    /// <summary>Toplu sıfırlamada üretilen geçici parola. Yalnızca bellekte, admin'e bir kez gösterilir.</summary>
    public record TempCredential(int UserId, string FullName, string Username, string TemporaryPassword)
    {
        /// <summary>Excel'de açılabilen UTF-8 (BOM'lu) CSV; ayırıcı ';' (tr-TR Excel).</summary>
        public static byte[] ToCsv(IEnumerable<TempCredential> items)
        {
            var sb = new StringBuilder();
            sb.Append("Ad Soyad;Kullanıcı Adı;Geçici Şifre\r\n");
            foreach (var c in items)
                sb.Append(Cell(c.FullName)).Append(';').Append(Cell(c.Username)).Append(';').Append(Cell(c.TemporaryPassword)).Append("\r\n");
            return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        }

        // Formül enjeksiyonu: '=', '+', '-', '@' ile başlayan hücre Excel'de formül olarak çalışır; ayrıca ayırıcı/tırnak kaçışı.
        private static string Cell(string value)
        {
            if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
            return value.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
