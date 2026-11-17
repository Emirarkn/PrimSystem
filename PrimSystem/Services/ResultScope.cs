namespace PrimSystem.Services
{
    /// <summary>
    /// Rolden sorgu kapsamını çözen saf yardımcı (DB'siz, test edilebilir).
    /// Servislerde <c>int? regionId</c> "null = filtre yok = tüm bölgeler" demektir. Bölgesi atanmamış bir BM
    /// için sayfalar RegionId'yi olduğu gibi (null) geçince BM tüm bölgelerin prim sonuçlarını görüyordu.
    /// Burada BM için null bölge, hiçbir satırla eşleşmeyen bir değere çevrilir (fail-closed).
    /// </summary>
    public static class ResultScope
    {
        /// <summary>Hiçbir bölgeyle eşleşmez (RegionId kimlikleri 1'den başlar). "Filtre yok" ile karışmasın diye null değil.</summary>
        public const int NoRegion = -1;

        /// <summary>BM → kendi bölgesi (yoksa <see cref="NoRegion"/>). Diğer roller → null (rolün kendi kuralı geçerli).</summary>
        public static int? RegionFilter(string roleName, int? userRegionId)
        {
            if (roleName != "BM") return null;
            return userRegionId is > 0 ? userRegionId : NoRegion;
        }
    }
}
