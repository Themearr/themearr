using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Themearr.API.Services;

namespace Themearr.API.Data;

public partial class Database
{
    private static void InitAudioRepairTables(SqliteConnection conn)
    {
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS audio_repair_runs (
                id TEXT PRIMARY KEY, state TEXT NOT NULL, options_json TEXT NOT NULL,
                options_hash TEXT NOT NULL, settings_hash TEXT NOT NULL,
                started_at TEXT NOT NULL, finished_at TEXT, error TEXT
            )
            """);
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS audio_repair_items (
                run_id TEXT NOT NULL, id TEXT NOT NULL, title TEXT NOT NULL, aliases_json TEXT NOT NULL,
                state TEXT NOT NULL, canonical_folder TEXT NOT NULL, source_path TEXT NOT NULL,
                final_path TEXT NOT NULL, backup_path TEXT NOT NULL, staging_path TEXT NOT NULL,
                input_hash TEXT NOT NULL, output_hash TEXT, revision INTEGER NOT NULL,
                before_bytes INTEGER NOT NULL, after_bytes INTEGER, backup_bytes INTEGER NOT NULL DEFAULT 0,
                error TEXT, warnings_json TEXT NOT NULL DEFAULT '[]', updated_at TEXT NOT NULL,
                PRIMARY KEY(run_id, id)
            )
            """);
        conn.Execute("CREATE INDEX IF NOT EXISTS idx_audio_repair_output ON audio_repair_items(canonical_folder, output_hash, state)");
    }

    internal List<AudioRepairCandidate> GetAudioRepairCandidates()
    {
        using var conn = Open(); var result = new List<AudioRepairCandidate>();
        foreach (var media in new[] { "movie", "show" })
            conn.Query($"""
                SELECT m.id, m.title, m.folderName, m.ignored, m.source, m.source_ref, m.recheck_state,
                    (SELECT h.picked_by FROM theme_history h WHERE h.movie_id = m.id AND h.media_type = @media ORDER BY h.id DESC LIMIT 1)
                FROM {TableFor(media)} m
                """, r => { while (r.Read()) result.Add(new(media,r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt32(3)!=0,
                    r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(7)?null:r.GetString(7),r.IsDBNull(6)?null:r.GetString(6))); }, ("@media",media));
        return result;
    }

    internal void CreateAudioRepairRun(AudioRepairStoredRun run, IReadOnlyList<AudioRepairStoredItem> items)
    {
        using var conn = Open(); using var transaction = conn.BeginTransaction(deferred:false);
        conn.Execute("INSERT INTO audio_repair_runs(id,state,options_json,options_hash,settings_hash,started_at) VALUES(@id,@state,@options,@hash,@settings,@at)",
            ("@id",run.Id.ToString()),("@state",run.State),("@options",run.OptionsJson),("@hash",run.OptionsHash),("@settings",run.SettingsHash),("@at",run.StartedAt.ToString("O")));
        foreach (var item in items)
            conn.Execute("""
                INSERT INTO audio_repair_items(run_id,id,title,aliases_json,state,canonical_folder,source_path,final_path,backup_path,staging_path,input_hash,revision,before_bytes,updated_at)
                VALUES(@run,@id,@title,@aliases,'planned',@folder,@source,@final,@backup,@staging,@input,@revision,@bytes,@at)
                """,("@run",run.Id.ToString()),("@id",item.Id),("@title",item.Title),("@aliases",JsonSerializer.Serialize(item.Aliases)),
                ("@folder",item.Folder),("@source",item.SourcePath),("@final",item.FinalPath),("@backup",item.BackupPath),("@staging",item.StagingPath),
                ("@input",item.InputHash),("@revision",item.Revision),("@bytes",item.BeforeBytes),("@at",DateTimeOffset.UtcNow.ToString("O")));
        transaction.Commit();
    }

    internal void UpdateAudioRepairItem(AudioRepairStoredItem item)
    {
        using var conn = Open(); WriteAudioRepairItem(conn,item);
    }
    private static void WriteAudioRepairItem(SqliteConnection conn, AudioRepairStoredItem item) => conn.Execute("""
        UPDATE audio_repair_items SET state=@state, output_hash=@hash, after_bytes=@after, backup_bytes=@backup,
            error=@error, warnings_json=@warnings, updated_at=@at WHERE run_id=@run AND id=@id
        """,("@state",item.State),("@hash",item.OutputHash),("@after",item.AfterBytes),("@backup",item.BackupBytes),("@error",item.Error),
        ("@warnings",JsonSerializer.Serialize(item.Warnings)),("@at",DateTimeOffset.UtcNow.ToString("O")),("@run",item.RunId.ToString()),("@id",item.Id));

    internal void CommitAudioRepairItem(AudioRepairStoredItem prepared, Action publishFiles, Action rollbackFiles)
    {
        using var conn = Open(); using var transaction = conn.BeginTransaction(deferred:false);
        try { publishFiles(); WriteAudioRepairItem(conn,prepared with { State="succeeded", Error=null }); transaction.Commit(); }
        catch { rollbackFiles(); throw; }
    }
    internal void FinishAudioRepairRun(Guid id, string state, string? error = null)
    {
        using var conn = Open(); conn.Execute("UPDATE audio_repair_runs SET state=@state,error=@error,finished_at=@at WHERE id=@id",
            ("@state",state),("@error",error),("@at",DateTimeOffset.UtcNow.ToString("O")),("@id",id.ToString()));
    }
    internal List<AudioRepairStoredRun> GetAudioRepairRuns()
    {
        using var conn = Open(); var result = new List<AudioRepairStoredRun>();
        conn.Query("SELECT id,state,options_json,options_hash,settings_hash,started_at,error FROM audio_repair_runs ORDER BY started_at DESC, rowid DESC",r =>
        { while(r.Read()) result.Add(new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),DateTimeOffset.Parse(r.GetString(5),CultureInfo.InvariantCulture),r.IsDBNull(6)?null:r.GetString(6))); });
        return result;
    }
    internal List<AudioRepairStoredItem> GetAudioRepairItems(Guid runId)
    {
        using var conn = Open(); var result = new List<AudioRepairStoredItem>();
        conn.Query("""
            SELECT id,title,aliases_json,state,canonical_folder,source_path,final_path,backup_path,staging_path,input_hash,output_hash,revision,before_bytes,after_bytes,backup_bytes,error,warnings_json
            FROM audio_repair_items WHERE run_id=@run ORDER BY rowid
            """,r => { while(r.Read()) result.Add(new() {
                RunId=runId,Id=r.GetString(0),Title=r.GetString(1),Aliases=JsonSerializer.Deserialize<List<AudioRepairCandidate>>(r.GetString(2)) ?? [],
                State=r.GetString(3),Folder=r.GetString(4),SourcePath=r.GetString(5),FinalPath=r.GetString(6),BackupPath=r.GetString(7),StagingPath=r.GetString(8),
                InputHash=r.GetString(9),OutputHash=r.IsDBNull(10)?null:r.GetString(10),Revision=r.GetInt64(11),BeforeBytes=r.GetInt64(12),AfterBytes=r.IsDBNull(13)?null:r.GetInt64(13),
                BackupBytes=r.GetInt64(14),Error=r.IsDBNull(15)?null:r.GetString(15),Warnings=JsonSerializer.Deserialize<List<string>>(r.GetString(16)) ?? [] }); },("@run",runId.ToString()));
        return result;
    }
    internal bool HasSuccessfulAudioRepair(string folder, string hash, string optionsHash)
    {
        using var conn = Open(); var found=false;
        conn.Query("""
            SELECT 1 FROM audio_repair_items i JOIN audio_repair_runs r ON r.id=i.run_id
            WHERE i.canonical_folder=@folder AND i.output_hash=@hash AND i.state='succeeded' AND r.options_hash=@options LIMIT 1
            """,r=>found=r.Read(),("@folder",folder),("@hash",hash),("@options",optionsHash));
        return found;
    }
}
