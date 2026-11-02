-- Forma reale dell'indice locale alla versione 6 (CreateProjectionSchema + meta/test_case_cache del commit dc675c3, estratti da git).
-- Solo DDL: i dati di prova sono copiati dalla proiezione corrente in LocalTestIndexLegacyMigrationTests.
CREATE TABLE IF NOT EXISTS meta(
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS test_case_cache(
    path TEXT PRIMARY KEY,
    uid TEXT NOT NULL,
    id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    eliminato INTEGER NOT NULL,
    mtime_utc_ticks INTEGER NOT NULL,
    file_length INTEGER NOT NULL,
    content_hash TEXT NOT NULL,
    json TEXT NOT NULL,
    indexed_utc_ticks INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_test_case_cache_uid ON test_case_cache(uid);
CREATE INDEX IF NOT EXISTS ix_test_case_cache_id ON test_case_cache(id);
CREATE INDEX IF NOT EXISTS ix_test_case_cache_eliminato ON test_case_cache(eliminato);
CREATE INDEX IF NOT EXISTS ix_test_case_cache_revision ON test_case_cache(revision);

CREATE TABLE IF NOT EXISTS test_summary(
    uid TEXT PRIMARY KEY,
    path TEXT NOT NULL UNIQUE,
    id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    eliminato INTEGER NOT NULL,
    data_creazione_ticks INTEGER NOT NULL,
    data_creazione_raw_ticks INTEGER NOT NULL,
    ultima_modifica_ticks INTEGER NOT NULL,
    modificato_da TEXT NOT NULL,
    stato_lavoro INTEGER NOT NULL,
    test_template_uid TEXT NOT NULL,
    creato_da TEXT NOT NULL,
    assegnatario TEXT,
    descrizione_breve TEXT NOT NULL,
    descrizione TEXT NOT NULL,
    descrizione_problema TEXT,
    severita INTEGER,
    priorita INTEGER NOT NULL,
    current_outcome INTEGER,
    revision_count INTEGER NOT NULL,
    operation_attempt_count INTEGER NOT NULL,
    effective_operation_count INTEGER NOT NULL,
    first_conforme_revision INTEGER
);

CREATE INDEX IF NOT EXISTS ix_test_summary_id ON test_summary(id);
CREATE INDEX IF NOT EXISTS ix_test_summary_eliminato ON test_summary(eliminato);
CREATE INDEX IF NOT EXISTS ix_test_summary_stato ON test_summary(stato_lavoro);
CREATE INDEX IF NOT EXISTS ix_test_summary_modificato ON test_summary(ultima_modifica_ticks DESC);
CREATE INDEX IF NOT EXISTS ix_test_summary_assegnatario ON test_summary(assegnatario);
CREATE INDEX IF NOT EXISTS ix_test_summary_template ON test_summary(test_template_uid);
CREATE INDEX IF NOT EXISTS ix_test_summary_outcome ON test_summary(current_outcome);
CREATE INDEX IF NOT EXISTS ix_test_summary_home ON test_summary(
    eliminato, data_creazione_ticks, uid, current_outcome, revision_count, first_conforme_revision);
CREATE INDEX IF NOT EXISTS ix_test_summary_work_page ON test_summary(
    eliminato, stato_lavoro, ultima_modifica_ticks DESC, id COLLATE NOCASE, uid);

CREATE TABLE IF NOT EXISTS test_ecu(
    test_uid TEXT NOT NULL,
    value TEXT NOT NULL COLLATE NOCASE,
    PRIMARY KEY(test_uid, value),
    FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_test_ecu_value ON test_ecu(value COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS test_vehicle(
    test_uid TEXT NOT NULL,
    value TEXT NOT NULL COLLATE NOCASE,
    PRIMARY KEY(test_uid, value),
    FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_test_vehicle_value ON test_vehicle(value COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS test_requirement(
    test_uid TEXT NOT NULL,
    value TEXT NOT NULL COLLATE NOCASE,
    PRIMARY KEY(test_uid, value),
    FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_test_requirement_value ON test_requirement(value COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS test_functionality(
    test_uid TEXT NOT NULL,
    value TEXT NOT NULL COLLATE NOCASE,
    PRIMARY KEY(test_uid, value),
    FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_test_functionality_value ON test_functionality(value COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS test_instrument(
    test_uid TEXT NOT NULL,
    value TEXT NOT NULL COLLATE NOCASE,
    PRIMARY KEY(test_uid, value),
    FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_test_instrument_value ON test_instrument(value COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS test_custom_field(
    test_uid TEXT NOT NULL,
    field_key TEXT NOT NULL,
    value TEXT NOT NULL,
    PRIMARY KEY(test_uid, field_key),
    FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_test_custom_field_values
    ON test_custom_field(field_key, value, test_uid);
CREATE TABLE IF NOT EXISTS test_revision(
    test_uid TEXT NOT NULL,
    revision_index INTEGER NOT NULL,
    iteration_number INTEGER NOT NULL,
    data_ticks INTEGER NOT NULL,
    operatore TEXT NOT NULL,
    outcome INTEGER NOT NULL,
    calculated_outcome INTEGER NOT NULL,
    raw_attempt_count INTEGER NOT NULL,
    effective_operation_count INTEGER NOT NULL,
    PRIMARY KEY(test_uid, revision_index),
    FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_test_revision_operator ON test_revision(operatore COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS ix_test_revision_home_operator ON test_revision(test_uid, operatore);
CREATE INDEX IF NOT EXISTS ix_test_revision_date ON test_revision(data_ticks);
CREATE INDEX IF NOT EXISTS ix_test_revision_outcome ON test_revision(outcome);

CREATE TABLE IF NOT EXISTS test_operation_attempt(
    test_uid TEXT NOT NULL,
    revision_index INTEGER NOT NULL,
    attempt_index INTEGER NOT NULL,
    global_attempt_index INTEGER NOT NULL,
    condition_uid TEXT NOT NULL,
    operation_root_uid TEXT NOT NULL,
    is_effective INTEGER NOT NULL,
    funzionalita TEXT NOT NULL COLLATE NOCASE,
    outcome INTEGER NOT NULL,
    operatore TEXT NOT NULL,
    data_ticks INTEGER NOT NULL,
    PRIMARY KEY(test_uid, revision_index, attempt_index),
    FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_operation_root ON test_operation_attempt(operation_root_uid);
CREATE INDEX IF NOT EXISTS ix_operation_functionality ON test_operation_attempt(funzionalita COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS ix_operation_operator ON test_operation_attempt(operatore COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS ix_operation_outcome ON test_operation_attempt(outcome);
CREATE INDEX IF NOT EXISTS ix_operation_date ON test_operation_attempt(data_ticks);

CREATE TABLE IF NOT EXISTS test_operation_ecu(
    test_uid TEXT NOT NULL,
    revision_index INTEGER NOT NULL,
    attempt_index INTEGER NOT NULL,
    componente TEXT NOT NULL COLLATE NOCASE,
    PRIMARY KEY(test_uid, revision_index, attempt_index, componente),
    FOREIGN KEY(test_uid) REFERENCES test_summary(uid) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_operation_ecu_value ON test_operation_ecu(componente COLLATE NOCASE);
