-- Spec 004 FR-003 (research R7): sign-in lockout per (account, client address) plus an
-- account-wide row (account, '*'). The account is owners.email_normalized; rows exist for unknown
-- emails too, so a failed sign-in looks the same whether or not the account exists.
CREATE TABLE signin_lockouts (
    account             text        NOT NULL,
    address             text        NOT NULL,
    failed_count        integer     NOT NULL DEFAULT 0,
    failed_window_start timestamptz,
    locked_until        timestamptz,
    PRIMARY KEY (account, address)
);
