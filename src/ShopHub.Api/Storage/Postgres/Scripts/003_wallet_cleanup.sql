-- Spec 004 T021 (FR-009): the lowest Wallet generation of a shop that may still exist in the
-- cluster. Generations below it are known deleted; the reconciler deletes oldest .. current-1 after
-- the Shop points at the current one, then raises this, so each old Wallet is deleted once.
ALTER TABLE shops ADD COLUMN oldest_wallet_generation integer NOT NULL DEFAULT 1;
ALTER TABLE shops ADD CONSTRAINT shops_oldest_wallet_generation_check
    CHECK (oldest_wallet_generation BETWEEN 1 AND wallet_generation);
