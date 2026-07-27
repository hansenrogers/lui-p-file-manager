# Lui-P File Manager - Implementation Kickoff

Date: 2026-07-27

## 1) Structured Requirements Specification

### 1.1 Core workflow
- Four-column desktop workflow:
  - Column A: Source location groups (configured roots)
  - Column B: Source file/folder browser for selected source
  - Column C: Destination groups and folders (configured destinations)
  - Column D: Destination content preview/list
- Primary action: copy from source to destination (never move/delete source in normal destination workflow).
- Drag-and-drop copy defaults from B to C/D, with clear copy affordances and invalid-target feedback.

### 1.2 Safety and recoverability
- No silent overwrite.
- Conflict dialog must offer Replace, Keep Both, Cancel.
- Destructive or overwriting operations require protected recovery data.
- Undo available from Activity Log for eligible operations for 30 days.
- Undo must validate current filesystem state before execution and refuse unsafe auto-undo.

### 1.3 File operations and UX parity
- Provide explorer-like actions: open, open with, copy, cut, paste, move, rename, delete, create folder, select all, properties, drag-and-drop, multi-select.
- Support keyboard shortcuts: Ctrl+C/X/V/A, F2, Delete, Shift+Delete, Enter, Alt+Enter, Ctrl+Z (eligible app actions).
- Delete requires explicit plain-language confirmation with scope and recovery info.

### 1.4 Activity Log and diagnostics
- User-facing Activity Log dashboard with structured operation records and Undo actions.
- Technical diagnostics log separated from Activity Log.
- Crash-resilient journaling: create operation journal before mutation, update progress while running, finalize after completion.

### 1.5 Search and indexing
- Index configured source and destination roots only.
- SQLite-backed index with fast partial-name search and date filters.
- Background indexing with progress, incremental updates, reconciliation, rebuild, per-location status.
- Search UI must remain responsive and verify target against live filesystem before destructive actions.

### 1.6 Preview
- Separate resizable pop-out preview window.
- Read-only previews for common document/image/media/text types where practical.
- Never execute active content.

### 1.7 Bottom shortcut bar
- Persistent, reorderable shortcuts for files/folders/programs.
- Removing a shortcut must never delete underlying content.
- Missing targets shown as unavailable with retry/locate/remove actions.

### 1.8 Shell integration
- Windows Explorer context menu: Copy with Lui-P File Manager -> destination submenu.
- Must use app copy engine and policy stack (conflicts, logging, recovery, progress).
- Clean install/uninstall behavior.

### 1.9 State persistence and accessibility
- Continuous debounced autosave of layout, filters, selections, ordering, preview state, and appearance.
- Crash recovery screen for interrupted operations.
- Older-user-first accessibility defaults (large text/targets, contrast, clear focus, keyboard and screen reader support).

### 1.10 Reliability and security
- Support long paths, large files/trees, locked/read-only/permission/disk/network edge cases.
- Streamed file IO, robust progress, post-copy verification.
- Path validation, traversal prevention, no credential storage, protected recovery store, safe logging/redaction.

## 2) Risky / Difficult Areas

- Correct, safe Undo across copy/move/replace/delete/create-folder under filesystem drift.
- Reliable indexing over network shares and Dropbox with eventual consistency.
- Crash-safe operation journal transitions across multi-file batch operations.
- Context-menu integration with destination submenu while preserving per-machine configuration.
- Preview support breadth without requiring Office and without executing active content.
- Performance tuning for large trees while keeping UI responsive.

## 3) Proposed Solution Architecture

## 3.1 Platform and stack
- Desktop: .NET 8 WPF (Windows 10/11 native desktop).
- Pattern: MVVM with service layer isolation.
- Data: SQLite (Microsoft.Data.Sqlite).
- Logging: Serilog rolling file logs for diagnostics.
- Optional packages later:
  - CommunityToolkit.Mvvm
  - SQLitePCLRaw.bundle_e_sqlite3
  - WindowsAPICodePack or Win32 interop helpers (as needed)

## 3.2 High-level modules
- UI Layer
  - MainWindow and screen views
  - Dialogs (conflict, delete confirm, undo result, recovery)
  - Accessibility and theme resources
- Application Layer
  - Command handlers and view models
  - Validation and policy orchestration
- Domain Services
  - FileOperationService
  - UndoService
  - RecoveryStoreService
  - ActivityLogService
  - IndexService
  - PreviewService
  - ShellIntegrationService
  - SettingsService
- Infrastructure
  - SQLite repositories
  - File hashing/metadata providers
  - FileSystemWatcher and scheduled reconciliation workers
  - Diagnostic logging and export

## 3.3 Process model
- UI thread remains non-blocking.
- Operation execution and indexing in background workers with cancellation tokens.
- Event bus (or mediator) for operation progress/state updates to UI and logs.

## 4) SQLite Schema (initial)

## 4.1 settings tables
- app_settings(key TEXT PRIMARY KEY, value TEXT, updated_utc TEXT)
- source_locations(id TEXT PRIMARY KEY, name TEXT, path TEXT, is_enabled INTEGER, sort_order INTEGER, created_utc TEXT, updated_utc TEXT)
- destination_locations(id TEXT PRIMARY KEY, name TEXT, path TEXT, is_enabled INTEGER, sort_order INTEGER, created_utc TEXT, updated_utc TEXT)
- shortcuts(id TEXT PRIMARY KEY, label TEXT, target_path TEXT, target_type TEXT, sort_order INTEGER, icon_pref TEXT, is_enabled INTEGER, created_utc TEXT, updated_utc TEXT)

## 4.2 activity and journal tables
- operations(
  operation_id TEXT PRIMARY KEY,
  batch_id TEXT,
  started_utc TEXT,
  completed_utc TEXT,
  windows_username TEXT,
  computer_name TEXT,
  operation_type TEXT,
  source_path TEXT,
  destination_path TEXT,
  item_name TEXT,
  item_count INTEGER,
  total_bytes INTEGER,
  status TEXT,
  error_code TEXT,
  error_message TEXT,
  undo_available INTEGER,
  undo_expiry_utc TEXT,
  undone_by_operation_id TEXT,
  related_operation_id TEXT
)
- operation_items(
  item_id TEXT PRIMARY KEY,
  operation_id TEXT,
  source_path TEXT,
  destination_path TEXT,
  action_type TEXT,
  status TEXT,
  bytes_total INTEGER,
  bytes_done INTEGER,
  checksum_before TEXT,
  checksum_after TEXT,
  metadata_before_json TEXT,
  metadata_after_json TEXT,
  recovery_blob_path TEXT,
  created_utc TEXT,
  updated_utc TEXT
)
- undo_attempts(
  undo_id TEXT PRIMARY KEY,
  target_operation_id TEXT,
  started_utc TEXT,
  completed_utc TEXT,
  result TEXT,
  reason_unavailable TEXT,
  details_json TEXT
)

## 4.3 index tables
- indexed_locations(location_id TEXT PRIMARY KEY, location_type TEXT, path TEXT, status TEXT, last_scan_utc TEXT, last_error TEXT)
- indexed_items(
  item_key TEXT PRIMARY KEY,
  location_id TEXT,
  full_path TEXT,
  parent_path TEXT,
  name TEXT,
  extension TEXT,
  is_folder INTEGER,
  size_bytes INTEGER,
  created_utc TEXT,
  modified_utc TEXT,
  availability_state TEXT,
  last_indexed_utc TEXT
)
- indexed_items_fts (FTS virtual table over name and full_path)

## 4.4 recommended indexes
- operations(started_utc)
- operations(status)
- operations(operation_type)
- operation_items(operation_id)
- indexed_items(location_id)
- indexed_items(parent_path)
- indexed_items(modified_utc)

## 5) Operation Journal and Undo Model

## 5.1 journal lifecycle
1. Create operation header with status=Planned.
2. Validate source/destination and permissions.
3. Transition to Running and write per-item intent records.
4. For each mutating step, capture pre-change metadata and recovery payload first.
5. Execute streamed IO.
6. Record verification markers (size, timestamps, optional hash).
7. Mark item Completed/Failed.
8. Mark operation Completed/PartialFailed/Failed.

## 5.2 protected recovery store
- Path: per-machine app data storage under ProgramData for shared machine visibility.
- Contents: versioned payloads for overwritten/deleted states and metadata snapshots.
- Access: ACL restricted to local users group and administrators (final ACL policy to be validated during implementation).
- Retention: purge data older than 30 days via scheduled cleanup job.

## 5.3 safe undo rules
- Validate item state before undo using metadata/hash heuristics.
- If destination changed since original operation and conflict risk exists, do not auto-overwrite.
- Offer safe alternatives (restore as copy with suffix, open folder, export recovery package).
- Always log undo attempts and outcomes.

## 6) Index Update Strategy

- Initial scan per configured location in background worker queue.
- Incremental updates via FileSystemWatcher where reliable.
- Periodic reconciliation scan (configurable interval) for missed network/Dropbox changes.
- Offline detection/backoff for unavailable locations.
- Automatic resume when location returns.
- Rebuild Index command drops and rebuilds index for selected location.
- Search path:
  - Query SQLite index for candidate set
  - Apply date filter and source section context
  - Before destructive action, re-validate live file state

## 7) Windows Context-Menu Approach

- Preferred route: Explorer command registration in HKCU for per-user install without admin requirement.
- Menu shape:
  - Copy with Lui-P File Manager
  - Child commands built from current configured destinations
- Invocation model:
  - Pass selected paths to app transfer entry point via command-line arguments.
  - App resolves destination alias to current machine configuration.
  - Reuse FileOperationService and ActivityLog pipeline.
- Uninstall:
  - Remove shell keys and command entries cleanly.

## 8) Screen-by-Screen UI Plan

- Main screen
  - Four-column layout with resize handles and persisted layout
  - Search/date filters per source section
  - Bottom shortcut bar
  - Main navigation: Activity Log, Settings, About/Diagnostics
- Activity Log + Undo dashboard
  - Filter/search toolbar
  - Expandable batch entries
  - Large Undo CTA with expiry text
  - Detail panel + export/retry actions
- Preview pop-out
  - Read-only viewer host
  - Prev/next, zoom/page/media controls
  - Open in default app, show in folder, properties
- Settings
  - Sources, destinations, index status, recovery storage, appearance, notifications, diagnostics, shell integration status
- Source location editor
  - Add/edit/remove/reorder, path validation, friendly warnings
- Destination location editor
  - Add/edit/remove/reorder, path validation, friendly warnings
- Index status
  - Per-location state, last scan, progress, rebuild actions
- Progress window
  - Current item, overall progress, counts, speed, cancel, summary
- Conflict dialog
  - Replace, Keep Both, Cancel, optional apply-to-all
- Delete confirmation
  - Plain-language risk summary, recoverability, default focus on Cancel
- Undo confirmation/result
  - Safety checks summary, success/failure reason, alternatives
- Interrupted operation recovery
  - Show interrupted jobs, retry/rollback/manual resolution options
- First run setup
  - Guided source/destination setup and accessibility defaults
- About + diagnostics
  - Version, runtime, logs, export diagnostics report

## 9) Phased Implementation Plan

### Phase 0 - Foundations (current)
- Finalize specification and architecture docs.
- Create initial project skeleton and module boundaries.

### Phase 1 - Shell app and core models
- Build WPF shell with four-column scaffold and navigation.
- Add settings storage and basic source/destination configuration.
- Add column resizing with persistence and reset layout.

### Phase 2 - Controlled file operations
- Implement FileOperationService with streamed copy/move/rename/create/delete wrappers.
- Add progress window, conflict dialog, delete confirmation.
- Enforce no silent overwrite and protected recovery pre-capture.

### Phase 3 - Activity log and undo
- Implement operation journaling and Activity Log dashboard.
- Implement undo engine with safety validation and expiry visibility.
- Add retry for failed/interrupted operations.

### Phase 4 - Search/index and preview
- Implement indexer, FTS queries, date filters, and index status UI.
- Implement preview pop-out and fallback metadata cards.

### Phase 5 - Shortcut bar and shell integration
- Implement bottom shortcut bar with persistence and missing-target handling.
- Implement Explorer context menu command and transfer entry point.

### Phase 6 - Accessibility, hardening, installer
- Accessibility pass, UIA names, keyboard traversal.
- Reliability and crash-recovery hardening.
- Build installer, upgrade/uninstall behavior, shell cleanup validation.

## 10) Blockers Requiring Owner Decision

No hard blocker prevents starting implementation.

Items that may need confirmation later (implementation can proceed with defaults now):
- Preferred UI platform if you want WinUI 3 instead of WPF.
- Whether shell integration should be per-user only by default or include optional machine-wide mode.
- Whether file hash verification should default to optional-on-large-files or always-on for all copies.

## 11) Immediate Start Actions Completed

- Requirements extracted from provided PDF.
- Kickoff architecture and data model document created.
- Ready to scaffold Phase 1 application structure next.

## 12) Current Delivery Snapshot

- Core operation engine now supports copy, delete, move, rename, and create-folder through the same journaling pipeline.
- Activity Log now supports retrying failed/canceled/partial-failed operations as new journaled operations.
- Partial-failed retries now support per-item source selection before execution.
- Multi-item failed/canceled retries now also support per-item source selection.
- Retry operations now use the progress window and support user cancellation.
- Activity Log now includes a selected-operation item details pane (kind/source/destination/recovery paths).
- Activity Log details pane now includes quick actions to open source/destination/recovery paths and copy the selected path.
- Activity Log action buttons are now state-aware (enabled only when valid for the current selection).
- Double-clicking an operation item now opens the best available path (destination, then recovery, then source).
- Undo coverage now includes moved items, renamed items, and created-folder removal (empty folder safety enforced).
- Automated undo regression suite now covers six scenarios (drift failures and success-path restores).
- Copy workflow now includes optional SHA-256 verification for large files (threshold-based).
- Checksum verification defaults (enabled flag and threshold MB) are now configurable and persisted across sessions.
- Operation records now persist full source-path lists to improve retry reconstruction accuracy.
- Multi-item source summaries now appear directly in operation list rows for quick scanning.
- Shell integration now installs a destination-aware submenu under Explorer:
  - Choose Destination...
  - Copy to {configured destination names}
- Shell integration installed-state detection now supports submenu-based registry layout and legacy command layout.
- Destination list changes in Settings refresh shell submenu entries when shell integration is already installed.
- Operation progress now reports estimated byte totals rather than item-count placeholders.
- Settings now includes an in-app local uninstall launcher that invokes the PowerShell uninstall script when available.
- Settings now includes an in-app local install launcher with desktop-shortcut and force-close options.
- Automated tests now include checksum-threshold copy verification coverage.

### Local packaging scripts

- Local install script: `scripts/install-local.ps1`
  - Copies published output to `%LOCALAPPDATA%\\LuiPFileManager\\app`
  - Creates Start Menu shortcut
  - Optional desktop shortcut switch
  - Optional force-close behavior if app is running
- Local uninstall script: `scripts/uninstall-local.ps1`
  - Stops running app process
  - Removes shortcuts and installed app files
  - Optional keep-shell switch or removal of Explorer integration key
  - Optional full app-data removal
- Script usage documentation: `scripts/README.md`
