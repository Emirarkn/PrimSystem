namespace PrimSystem.Services
{
    /// <summary>
    /// Geçici şifreyle giriş yapmış kullanıcıyı şifre değiştirme sayfasına zorlar (DB'siz, test edilebilir).
    /// MustChangePassword daha önce yalnızca giriş sonrası yönlendirmeyi seçiyordu; kullanıcı adresi elle
    /// yazıp geçici şifreyle süresiz çalışabiliyordu.
    /// </summary>
    public static class PasswordChangeGate
    {
        public const string ChangePasswordPath = "/change-password";

        // Giriş yapılmadan açılan sayfalar ve hata sayfası zorlamadan muaf (şifre sıfırlama bağlantısı vb.).
        private static readonly string[] ExemptPaths =
            { ChangePasswordPath, "/login", "/forgot-password", "/reset-password", "/error" };

        /// <summary>Yönlendirilecek yol; zorlama gerekmiyorsa null.</summary>
        /// <param name="relativePath">Uygulama köküne göre yol, sorgu ve parça olmadan ("calculate", "/calculate", "").</param>
        /// <param name="isImpersonating">Taklit sırasında CurrentUser hedef kullanıcıdır; yönetici onun bayrağıyla kilitlenmemeli.</param>
        public static string? RedirectTarget(bool isLoggedIn, bool mustChangePassword, bool isImpersonating, string relativePath)
        {
            if (!isLoggedIn || !mustChangePassword || isImpersonating) return null;
            var path = "/" + (relativePath ?? "").Trim('/').ToLowerInvariant();
            return Array.IndexOf(ExemptPaths, path) >= 0 ? null : ChangePasswordPath;
        }
    }
}
