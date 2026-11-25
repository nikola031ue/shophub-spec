-- Spec 004 data-model.md "ShopHub database": owners, sessions, Shop records, Data-Protection keys.
-- signin_lockouts came in 001.

CREATE TABLE owners (
    id               uuid        PRIMARY KEY,
    email            text        NOT NULL CHECK (length(email) BETWEEN 3 AND 254),
    email_normalized text        NOT NULL UNIQUE CHECK (email_normalized = lower(trim(email))),
    password_hash    text        NOT NULL,
    created_at       timestamptz NOT NULL
);

CREATE TABLE owner_sessions (
    id         uuid        PRIMARY KEY,
    owner_id   uuid        NOT NULL REFERENCES owners (id) ON DELETE CASCADE,
    created_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    CHECK (expires_at > created_at)
);

CREATE INDEX owner_sessions_owner_idx ON owner_sessions (owner_id);

CREATE TABLE shops (
    id                text        PRIMARY KEY CHECK (id ~ '^s[a-z2-7]{9}$'),
    owner_id          uuid        NOT NULL REFERENCES owners (id),
    display_name      text        NOT NULL CHECK (length(display_name) BETWEEN 1 AND 100),
    display_name_key  text        NOT NULL CHECK (display_name_key = lower(trim(display_name))),
    availability      text        NOT NULL CHECK (availability IN ('standard', 'high')),
    database          text        NOT NULL CHECK (database IN ('standard', 'light')),
    wallet_address    text        NOT NULL CHECK (wallet_address ~ '^0x[0-9a-fA-F]{40}$'),
    wallet_generation integer     NOT NULL DEFAULT 1 CHECK (wallet_generation >= 1),
    state             text        NOT NULL CHECK (state IN ('Active', 'Deleting')),
    created_at        timestamptz NOT NULL,
    updated_at        timestamptz NOT NULL,
    UNIQUE (owner_id, display_name_key)
);

CREATE INDEX shops_owner_idx ON shops (owner_id);

CREATE TABLE data_protection_keys (
    id         text        PRIMARY KEY,
    xml        text        NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
