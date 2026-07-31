/* ============================================================================
   PrimSystem — Ürün InfiniLens Alias Düzeltmesi (DİĞER İTRİYAT grubu)
   ----------------------------------------------------------------------------
   SORUN: Sell-out (InfiniLens) marka adları hedef ürün adından farklı; eşleme
   Product.ImportAlias ile yapılıyor. 'DİĞER İTRİYAT (JB GRUBU / NT GRUBU)' (P13)
   ürününün alias'ı yanlışlıkla yalnız CAREFREE'ydi → JOHNSON'S BABY + NEUTROGENA
   sell-out hacmi prim-dışı P14/P15'e düşüyor, DİĞER İTRİYAT ≈0 ölçülüyordu.

   ÇÖZÜM: P13 alias'ı üç markayı (';' ile ayrık) toplar; çakışan/fazlalık prim-dışı
   ürünler (JBABY, NEUTROGENA, MAUI) pasifleştirilir ki alias'ları P13'ten çalmasın.
   Yeni sürümün çoklu-alias kodu bu ';' ayrımını çözer.

   Çalıştırma:  sqlcmd -S <sunucu> -d PrimDB -i product-alias-fix.sql
            ya da SSMS'te PrimDB üzerinde açıp Execute (dosyayı UTF-8 aç).
   Idempotent — tekrar çalıştırmak güvenlidir.

   NOT: ProductId'ler bu DB'ye göre sabittir (P13=DİĞER İTRİYAT, P14=JBABY,
   P15=NEUTROGENA, P16=MAUI). Şüphede aşağıdaki ilk SELECT ile doğrulayın.

   GÜNCELLEME — Aveeno birleştirmesi: Aveeno (P1) artık ayrı prim kalemi değil,
   DİĞER İTRİYAT'a dahil. Aşağıdaki alias sabitine AVEENO eklendi; aksi halde bu
   script tekrar çalıştırıldığında P13'ün alias'ından AVEENO'yu siler ve Aveeno
   çıkışları tekrar prim dışına düşerdi. Veri taşıması ayrı: 'aveeno-merge.sql'.
   ============================================================================ */

SET NOCOUNT ON;

/* 0) Mevcut durumu göster */
PRINT '--- ÖNCE ---';
SELECT ProductId, IsActive, IsPrimEligible, Name, ImportAlias
FROM dbo.Products
WHERE ProductId IN (13, 14, 15, 16)
ORDER BY ProductId;

/* 1) DİĞER İTRİYAT (P13): JB + NT + CAREFREE + AVEENO markalarını topla */
UPDATE dbo.Products
SET ImportAlias = N'JOHNSON''S BABY;NEUTROGENA;CAREFREE;AVEENO'
WHERE ProductId = 13
  AND (ImportAlias IS NULL OR ImportAlias <> N'JOHNSON''S BABY;NEUTROGENA;CAREFREE;AVEENO');

/* 2) Çakışan/fazlalık prim-dışı ürünleri pasifle (alias'ları P13 ile çakışır) */
UPDATE dbo.Products
SET IsActive = 0
WHERE ProductId IN (14, 15, 16)
  AND IsActive = 1;

/* 3) Doğrulama */
PRINT '--- SONRA ---';
SELECT ProductId, IsActive, IsPrimEligible, Name, ImportAlias
FROM dbo.Products
WHERE ProductId IN (13, 14, 15, 16)
ORDER BY ProductId;

PRINT 'Alias düzeltmesi uygulandı. SONRAKİ ADIM: Q2 sell-out''u replaceExisting ile yeniden yükle + yeniden hesapla.';
