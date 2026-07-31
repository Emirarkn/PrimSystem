-- Eczane Analizi performansı: SellOutDetails GLN-bazında toplama için kapsayıcı (covering) indeks.
-- Dönem filtreli "GROUP BY GLN, SUM(Quantity/AmountTL)" sorgusunu key-lookup'sız çalıştırır
-- (~20 sn → ~1-2 sn). Idempotent — tekrar çalıştırmak güvenli.

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SellOutDetails_Period_Incl' AND object_id = OBJECT_ID('dbo.SellOutDetails'))
    CREATE INDEX IX_SellOutDetails_Period_Incl
        ON dbo.SellOutDetails (PeriodId)
        INCLUDE (GLN, BrickId, Quantity, AmountTL, KurumName, Il, Ilce);
GO
