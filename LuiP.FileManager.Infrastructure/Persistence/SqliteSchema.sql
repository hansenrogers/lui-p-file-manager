CREATE TABLE IF NOT EXISTS operations (
    operation_id TEXT PRIMARY KEY,
    started_utc TEXT NOT NULL,
    completed_utc TEXT NULL,
    operation_type INTEGER NOT NULL,
    status INTEGER NOT NULL,
    item_count INTEGER NOT NULL,
    total_bytes INTEGER NOT NULL,
    source_path TEXT NULL,
    source_paths_json TEXT NULL,
    destination_path TEXT NULL,
    undo_available INTEGER NOT NULL,
    undo_expiry_utc TEXT NULL,
    error_message TEXT NULL
);

CREATE TABLE IF NOT EXISTS operation_items (
    item_id TEXT PRIMARY KEY,
    operation_id TEXT NOT NULL,
    kind INTEGER NOT NULL,
    source_path TEXT NOT NULL,
    destination_path TEXT NULL,
    recovery_path TEXT NULL,
    destination_size_bytes INTEGER NULL,
    destination_last_write_utc TEXT NULL,
    FOREIGN KEY(operation_id) REFERENCES operations(operation_id)
);

CREATE TABLE IF NOT EXISTS configured_locations (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    path TEXT NOT NULL,
    kind INTEGER NOT NULL,
    is_enabled INTEGER NOT NULL,
    sort_order INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS indexed_locations (
    location_id TEXT PRIMARY KEY,
    kind INTEGER NOT NULL,
    name TEXT NOT NULL,
    path TEXT NOT NULL,
    status TEXT NOT NULL,
    last_indexed_utc TEXT NULL,
    last_error TEXT NULL
);

CREATE TABLE IF NOT EXISTS indexed_items (
    item_key TEXT PRIMARY KEY,
    location_id TEXT NOT NULL,
    full_path TEXT NOT NULL,
    parent_path TEXT NOT NULL,
    name TEXT NOT NULL,
    extension TEXT NULL,
    size_bytes INTEGER NULL,
    created_utc TEXT NULL,
    modified_utc TEXT NULL,
    is_folder INTEGER NOT NULL,
    last_indexed_utc TEXT NOT NULL,
    FOREIGN KEY(location_id) REFERENCES indexed_locations(location_id)
);

CREATE TABLE IF NOT EXISTS shortcuts (
    id TEXT PRIMARY KEY,
    label TEXT NOT NULL,
    target_path TEXT NOT NULL,
    target_kind INTEGER NOT NULL,
    sort_order INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_operations_started_utc ON operations(started_utc DESC);
CREATE INDEX IF NOT EXISTS idx_operations_status ON operations(status);
CREATE INDEX IF NOT EXISTS idx_operations_type ON operations(operation_type);
CREATE INDEX IF NOT EXISTS idx_operation_items_operation_id ON operation_items(operation_id);
CREATE INDEX IF NOT EXISTS idx_configured_locations_kind_order ON configured_locations(kind, sort_order);
CREATE INDEX IF NOT EXISTS idx_indexed_items_location ON indexed_items(location_id);
CREATE INDEX IF NOT EXISTS idx_indexed_items_name ON indexed_items(name);
CREATE INDEX IF NOT EXISTS idx_indexed_items_modified ON indexed_items(modified_utc);
CREATE INDEX IF NOT EXISTS idx_shortcuts_sort ON shortcuts(sort_order);
