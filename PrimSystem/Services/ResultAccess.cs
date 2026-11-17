using PrimSystem.Data;

namespace PrimSystem.Services
{
    /// <summary>Sonuç sorgusuna uygulanacak, aktörden türetilmiş kapsam.</summary>
    public readonly record struct ResultQuery(int? RegionId, int? UserId, bool PublishedOnly);

    /// <summary>
    /// Bir aktörün hangi prim sonuçlarını okuyabileceğini rolünden türetir (DB'siz, test edilebilir).
    /// Daha önce kapsam sayfalardan nullable filtre olarak geliyordu; null "filtre yok" demek olduğu için
    /// sayfadaki bir eksik filtre tüm bölgelerin/kişilerin primini döndürebiliyordu. Artık BM ve ST için
    /// çağıranın verdiği bölge/yayın filtresi dikkate alınmaz, aktörün kendi bilgisinden hesaplanır.
    /// </summary>
    public static class ResultAccess
    {
        /// <summary>Hiçbir satırla eşleşmez: bilinmeyen rol, pasif veya silinmiş aktör.</summary>
        public static readonly ResultQuery Denied = new(ResultScope.NoRegion, -1, true);

        public static ResultQuery Resolve(
            int roleId, int actorUserId, int? actorRegionId,
            bool requestedPublishedOnly, int? requestedRegionId, int? requestedUserId)
        {
            return roleId switch
            {
                // Admin ve Yönetici tüm bölgeleri görür; istenirse daraltabilir ve taslak sonuçları görebilir.
                RoleId.Admin or RoleId.Yonetici => new(requestedRegionId, requestedUserId, requestedPublishedOnly),
                // BM yalnız kendi bölgesinin YAYINLANMIŞ sonuçlarını görür (bölgesi yoksa hiçbirini).
                RoleId.BM => new(ResultScope.RegionFilter("BM", actorRegionId), requestedUserId, true),
                // ST yalnız kendi yayınlanmış satırını görür.
                RoleId.ST => new(null, actorUserId, true),
                _ => Denied
            };
        }
    }
}
