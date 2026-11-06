-- RemoteConnectionManager shared database, schema version 1.
-- Applied by the app (Settings > Storage > Create or upgrade) as a member of db_owner.
-- Batches are split on GO lines. Each script runs in one transaction.
--
-- Security model:
--   * Everyone gets in through the rcm_user role, which only has EXECUTE on the rcm schema.
--     No one in rcm_user can read or write a table directly; every procedure checks the
--     caller (SUSER_SNAME()) against RCM's own users, groups and folder permissions.
--   * RCM admins (rcm.Users.IsAdmin, or db_owner) see and change everything.
--   * A folder's level for a user is the highest level any of their groups has on it or on
--     the folders above it, unless the folder breaks inheritance. No permission means no access.

IF (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) < 130
    THROW 50004, N'RemoteConnectionManager needs database compatibility level 130 (SQL Server 2016) or higher.', 1;
GO
IF SCHEMA_ID(N'rcm') IS NULL EXEC(N'CREATE SCHEMA rcm AUTHORIZATION dbo');
GO
IF DATABASE_PRINCIPAL_ID(N'rcm_user') IS NULL CREATE ROLE rcm_user AUTHORIZATION dbo;
GO

CREATE TABLE rcm.SchemaVersion
(
    Version     int            NOT NULL CONSTRAINT PK_SchemaVersion PRIMARY KEY,
    AppliedUtc  datetime2(0)   NOT NULL CONSTRAINT DF_SchemaVersion_AppliedUtc DEFAULT SYSUTCDATETIME(),
    AppliedBy   nvarchar(256)  NOT NULL CONSTRAINT DF_SchemaVersion_AppliedBy DEFAULT SUSER_SNAME()
);

CREATE TABLE rcm.Users
(
    Id          int IDENTITY   NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    Login       nvarchar(256)  NOT NULL CONSTRAINT UQ_Users_Login UNIQUE,
    DisplayName nvarchar(256)  NULL,
    IsAdmin     bit            NOT NULL CONSTRAINT DF_Users_IsAdmin DEFAULT 0,
    CreatedUtc  datetime2(0)   NOT NULL CONSTRAINT DF_Users_CreatedUtc DEFAULT SYSUTCDATETIME(),
    LastSeenUtc datetime2(0)   NULL
);

CREATE TABLE rcm.Groups
(
    Id          int IDENTITY   NOT NULL CONSTRAINT PK_Groups PRIMARY KEY,
    Name        nvarchar(128)  NOT NULL CONSTRAINT UQ_Groups_Name UNIQUE,
    Description nvarchar(1000) NULL
);

CREATE TABLE rcm.GroupMembers
(
    GroupId int NOT NULL CONSTRAINT FK_GroupMembers_Group REFERENCES rcm.Groups (Id) ON DELETE CASCADE,
    UserId  int NOT NULL CONSTRAINT FK_GroupMembers_User REFERENCES rcm.Users (Id) ON DELETE CASCADE,
    CONSTRAINT PK_GroupMembers PRIMARY KEY (GroupId, UserId)
);

CREATE TABLE rcm.Folders
(
    Id               uniqueidentifier NOT NULL CONSTRAINT PK_Folders PRIMARY KEY NONCLUSTERED,
    ParentId         uniqueidentifier NULL CONSTRAINT FK_Folders_Parent REFERENCES rcm.Folders (Id),
    Name             nvarchar(256)    NOT NULL,
    SortOrder        int              NOT NULL CONSTRAINT DF_Folders_SortOrder DEFAULT 0,
    -- JSON: description and inherited defaults, the same shape as connections.json.
    Body             nvarchar(max)    NOT NULL,
    BreakInheritance bit              NOT NULL CONSTRAINT DF_Folders_BreakInheritance DEFAULT 0,
    RowVersion       rowversion       NOT NULL,
    ModifiedBy       nvarchar(256)    NOT NULL CONSTRAINT DF_Folders_ModifiedBy DEFAULT SUSER_SNAME(),
    ModifiedUtc      datetime2(0)     NOT NULL CONSTRAINT DF_Folders_ModifiedUtc DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Folders_ParentId ON rcm.Folders (ParentId);

CREATE TABLE rcm.Entries
(
    Id          uniqueidentifier NOT NULL CONSTRAINT PK_Entries PRIMARY KEY NONCLUSTERED,
    FolderId    uniqueidentifier NULL CONSTRAINT FK_Entries_Folder REFERENCES rcm.Folders (Id),
    Kind        nvarchar(32)     NOT NULL,
    Name        nvarchar(256)    NOT NULL,
    SortOrder   int              NOT NULL CONSTRAINT DF_Entries_SortOrder DEFAULT 0,
    -- JSON: the whole entry, the same shape as connections.json (never a password).
    Body        nvarchar(max)    NOT NULL,
    RowVersion  rowversion       NOT NULL,
    ModifiedBy  nvarchar(256)    NOT NULL CONSTRAINT DF_Entries_ModifiedBy DEFAULT SUSER_SNAME(),
    ModifiedUtc datetime2(0)     NOT NULL CONSTRAINT DF_Entries_ModifiedUtc DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Entries_FolderId ON rcm.Entries (FolderId);

CREATE TABLE rcm.FolderPermissions
(
    FolderId uniqueidentifier NOT NULL CONSTRAINT FK_FolderPermissions_Folder REFERENCES rcm.Folders (Id) ON DELETE CASCADE,
    GroupId  int              NOT NULL CONSTRAINT FK_FolderPermissions_Group REFERENCES rcm.Groups (Id) ON DELETE CASCADE,
    -- 1 View, 2 Edit, 3 Manage (change permissions, move the folder).
    Level    tinyint          NOT NULL CONSTRAINT CK_FolderPermissions_Level CHECK (Level BETWEEN 1 AND 3),
    CONSTRAINT PK_FolderPermissions PRIMARY KEY (FolderId, GroupId)
);

CREATE TABLE rcm.DocumentSettings
(
    Id       tinyint       NOT NULL CONSTRAINT PK_DocumentSettings PRIMARY KEY CONSTRAINT CK_DocumentSettings_Single CHECK (Id = 1),
    Defaults nvarchar(max) NOT NULL CONSTRAINT DF_DocumentSettings_Defaults DEFAULT N'{}'
);
INSERT rcm.DocumentSettings (Id) VALUES (1);

CREATE TABLE rcm.UserPreferences
(
    UserId int           NOT NULL CONSTRAINT FK_UserPreferences_User REFERENCES rcm.Users (Id) ON DELETE CASCADE,
    [Key]  nvarchar(128) NOT NULL,
    Value  nvarchar(max) NOT NULL,
    CONSTRAINT PK_UserPreferences PRIMARY KEY (UserId, [Key])
);

CREATE TABLE rcm.UserEntrySettings
(
    UserId   int              NOT NULL CONSTRAINT FK_UserEntrySettings_User REFERENCES rcm.Users (Id) ON DELETE CASCADE,
    TargetId uniqueidentifier NOT NULL,
    Body     nvarchar(max)    NOT NULL,
    CONSTRAINT PK_UserEntrySettings PRIMARY KEY (UserId, TargetId)
);

-- Who changed what. Clients poll MAX(Id) to know when to reload.
CREATE TABLE rcm.ChangeLog
(
    Id         bigint IDENTITY NOT NULL CONSTRAINT PK_ChangeLog PRIMARY KEY,
    ChangedUtc datetime2(0)    NOT NULL CONSTRAINT DF_ChangeLog_ChangedUtc DEFAULT SYSUTCDATETIME(),
    ChangedBy  nvarchar(256)   NOT NULL CONSTRAINT DF_ChangeLog_ChangedBy DEFAULT SUSER_SNAME(),
    Action     nvarchar(32)    NOT NULL,
    TargetKind nvarchar(16)    NOT NULL,
    TargetId   nvarchar(64)    NULL,
    TargetName nvarchar(256)   NULL
);
GO

-- ---------- helpers ----------

CREATE FUNCTION rcm.fn_IsAdmin()
RETURNS bit
AS
BEGIN
    IF IS_MEMBER(N'db_owner') = 1 RETURN 1;
    IF EXISTS (SELECT 1 FROM rcm.Users WHERE Login = SUSER_SNAME() AND IsAdmin = 1) RETURN 1;
    RETURN 0;
END
GO

-- Every folder with the caller's effective level (0 = none). Admins get 3 everywhere.
CREATE FUNCTION rcm.fn_MyFolderLevels()
RETURNS TABLE
AS
RETURN
WITH me AS
(
    SELECT Id FROM rcm.Users WHERE Login = SUSER_SNAME()
),
own AS
(
    SELECT fp.FolderId, MAX(fp.Level) AS Lvl
    FROM rcm.FolderPermissions fp
    JOIN rcm.GroupMembers gm ON gm.GroupId = fp.GroupId
    JOIN me ON me.Id = gm.UserId
    GROUP BY fp.FolderId
),
f AS
(
    SELECT f.Id, f.ParentId, f.BreakInheritance, ISNULL(o.Lvl, 0) AS OwnLvl
    FROM rcm.Folders f
    LEFT JOIN own o ON o.FolderId = f.Id
),
tree AS
(
    SELECT f.Id, CAST(f.OwnLvl AS tinyint) AS Lvl
    FROM f
    WHERE f.ParentId IS NULL
    UNION ALL
    SELECT c.Id, CAST(CASE WHEN c.BreakInheritance = 1 OR c.OwnLvl > t.Lvl THEN c.OwnLvl ELSE t.Lvl END AS tinyint)
    FROM f c
    JOIN tree t ON c.ParentId = t.Id
)
SELECT tree.Id AS FolderId,
       CASE WHEN rcm.fn_IsAdmin() = 1 THEN CAST(3 AS tinyint) ELSE tree.Lvl END AS Lvl
FROM tree;
GO

-- The caller's level on one folder. NULL means the top level, which only admins may change.
CREATE FUNCTION rcm.fn_FolderLevel(@FolderId uniqueidentifier)
RETURNS tinyint
AS
BEGIN
    IF rcm.fn_IsAdmin() = 1 RETURN 3;
    IF @FolderId IS NULL RETURN 0;
    RETURN ISNULL((SELECT Lvl FROM rcm.fn_MyFolderLevels() WHERE FolderId = @FolderId), 0);
END
GO

CREATE PROCEDURE rcm.RequireAdmin
AS
BEGIN
    IF rcm.fn_IsAdmin() = 0
        THROW 50003, N'Only an RCM admin can do that.', 1;
END
GO

-- ---------- session ----------

CREATE PROCEDURE rcm.GetSchemaVersion
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ISNULL(MAX(Version), 0) FROM rcm.SchemaVersion;
END
GO

-- Registers the caller on first use and says who they are.
CREATE PROCEDURE rcm.WhoAmI
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @login nvarchar(256) = SUSER_SNAME();
    IF EXISTS (SELECT 1 FROM rcm.Users WHERE Login = @login)
        UPDATE rcm.Users SET LastSeenUtc = SYSUTCDATETIME() WHERE Login = @login;
    ELSE
    BEGIN
        BEGIN TRY
            INSERT rcm.Users (Login, LastSeenUtc) VALUES (@login, SYSUTCDATETIME());
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() NOT IN (2601, 2627) THROW; -- another session registered the same login first
        END CATCH;
    END;

    SELECT u.Id, u.Login, u.DisplayName, rcm.fn_IsAdmin() AS IsAdmin,
           (SELECT ISNULL(MAX(Version), 0) FROM rcm.SchemaVersion) AS SchemaVersion
    FROM rcm.Users u
    WHERE u.Login = @login;
END
GO

CREATE PROCEDURE rcm.GetChangeStamp
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ISNULL(MAX(Id), 0) FROM rcm.ChangeLog;
END
GO

-- ---------- tree ----------

-- Result sets: document defaults; visible folders with the caller's level; visible entries; change stamp.
CREATE PROCEDURE rcm.GetTree
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @admin bit = rcm.fn_IsAdmin();
    DECLARE @stamp bigint = (SELECT ISNULL(MAX(Id), 0) FROM rcm.ChangeLog);

    SELECT FolderId, Lvl INTO #lv FROM rcm.fn_MyFolderLevels() WHERE Lvl >= 1;

    SELECT Defaults FROM rcm.DocumentSettings WHERE Id = 1;

    SELECT f.Id, f.ParentId, f.Name, f.SortOrder, f.Body, f.BreakInheritance, f.RowVersion, f.ModifiedBy, f.ModifiedUtc, lv.Lvl AS MyLevel
    FROM rcm.Folders f
    JOIN #lv lv ON lv.FolderId = f.Id;

    SELECT e.Id, e.FolderId, e.Kind, e.Name, e.SortOrder, e.Body, e.RowVersion, e.ModifiedBy, e.ModifiedUtc
    FROM rcm.Entries e
    WHERE (e.FolderId IS NULL AND @admin = 1)
       OR EXISTS (SELECT 1 FROM #lv lv WHERE lv.FolderId = e.FolderId);

    SELECT @stamp;
END
GO

CREATE PROCEDURE rcm.SaveDocumentDefaults
    @Defaults nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC rcm.RequireAdmin;
    UPDATE rcm.DocumentSettings SET Defaults = @Defaults WHERE Id = 1;
    INSERT rcm.ChangeLog (Action, TargetKind) VALUES (N'update', N'defaults');
END
GO

CREATE PROCEDURE rcm.SaveFolder
    @Id uniqueidentifier,
    @ParentId uniqueidentifier,
    @Name nvarchar(256),
    @SortOrder int,
    @Body nvarchar(max),
    @ExpectedVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @msg nvarchar(2048);
    DECLARE @exists bit = 0, @curParent uniqueidentifier, @curVersion binary(8), @curName nvarchar(256), @by nvarchar(256), @at datetime2(0);

    BEGIN TRAN;
    SELECT @exists = 1, @curParent = ParentId, @curVersion = RowVersion, @curName = Name, @by = ModifiedBy, @at = ModifiedUtc
    FROM rcm.Folders WITH (UPDLOCK, HOLDLOCK)
    WHERE Id = @Id;

    IF @exists = 0
    BEGIN
        IF @ExpectedVersion IS NOT NULL
        BEGIN
            SET @msg = CONCAT(N'Folder ''', @Name, N''' was deleted by someone else.');
            THROW 50001, @msg, 1;
        END;
        IF @ParentId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM rcm.Folders WHERE Id = @ParentId)
        BEGIN
            SET @msg = CONCAT(N'The folder that ''', @Name, N''' goes in was deleted by someone else.');
            THROW 50001, @msg, 1;
        END;
        IF rcm.fn_FolderLevel(@ParentId) < 2
        BEGIN
            SET @msg = CASE WHEN @ParentId IS NULL THEN N'Only an RCM admin can add folders at the top level.'
                            ELSE CONCAT(N'You cannot add folder ''', @Name, N''' there: you need Edit on the folder it goes in.') END;
            THROW 50003, @msg, 1;
        END;
        INSERT rcm.Folders (Id, ParentId, Name, SortOrder, Body) VALUES (@Id, @ParentId, @Name, @SortOrder, @Body);
        INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName) VALUES (N'add', N'folder', CONVERT(nvarchar(64), @Id), @Name);
    END
    ELSE
    BEGIN
        IF @ExpectedVersion IS NULL OR @ExpectedVersion <> @curVersion
        BEGIN
            SET @msg = CONCAT(N'Folder ''', @curName, N''' was changed by ', @by, N' at ', FORMAT(@at, N'yyyy-MM-dd HH:mm'), N' UTC.');
            THROW 50001, @msg, 1;
        END;
        IF rcm.fn_FolderLevel(@Id) < 2
        BEGIN
            SET @msg = CONCAT(N'You cannot change folder ''', @curName, N''': you need Edit on it.');
            THROW 50003, @msg, 1;
        END;
        IF (@ParentId IS NULL AND @curParent IS NOT NULL) OR (@ParentId IS NOT NULL AND (@curParent IS NULL OR @ParentId <> @curParent))
        BEGIN
            IF rcm.fn_FolderLevel(@Id) < 3 OR rcm.fn_FolderLevel(@ParentId) < 2
            BEGIN
                SET @msg = CONCAT(N'You cannot move folder ''', @curName, N''': you need Manage on it and Edit on where it goes.');
                THROW 50003, @msg, 1;
            END;
            DECLARE @cycle bit = 0;
            WITH up AS
            (
                SELECT Id, ParentId FROM rcm.Folders WHERE Id = @ParentId
                UNION ALL
                SELECT f.Id, f.ParentId FROM rcm.Folders f JOIN up ON f.Id = up.ParentId
            )
            SELECT @cycle = 1 FROM up WHERE Id = @Id;
            IF @cycle = 1
                THROW 50004, N'A folder cannot be moved inside itself.', 1;
        END;
        UPDATE rcm.Folders
        SET ParentId = @ParentId, Name = @Name, SortOrder = @SortOrder, Body = @Body,
            ModifiedBy = SUSER_SNAME(), ModifiedUtc = SYSUTCDATETIME()
        WHERE Id = @Id;
        INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName) VALUES (N'update', N'folder', CONVERT(nvarchar(64), @Id), @Name);
    END;

    SELECT RowVersion FROM rcm.Folders WHERE Id = @Id;
    COMMIT;
END
GO

-- Deletes a folder with everything in it. Refused if the caller cannot edit every folder inside.
CREATE PROCEDURE rcm.DeleteFolder
    @Id uniqueidentifier,
    @ExpectedVersion binary(8)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @msg nvarchar(2048);
    DECLARE @exists bit = 0, @parent uniqueidentifier, @curVersion binary(8), @name nvarchar(256), @by nvarchar(256), @at datetime2(0);

    BEGIN TRAN;
    SELECT @exists = 1, @parent = ParentId, @curVersion = RowVersion, @name = Name, @by = ModifiedBy, @at = ModifiedUtc
    FROM rcm.Folders WITH (UPDLOCK, HOLDLOCK)
    WHERE Id = @Id;

    IF @exists = 0
    BEGIN
        COMMIT; -- already gone, which is what was wanted
        RETURN;
    END;
    IF @ExpectedVersion IS NULL OR @ExpectedVersion <> @curVersion
    BEGIN
        SET @msg = CONCAT(N'Folder ''', @name, N''' was changed by ', @by, N' at ', FORMAT(@at, N'yyyy-MM-dd HH:mm'), N' UTC.');
        THROW 50001, @msg, 1;
    END;
    IF rcm.fn_FolderLevel(@Id) < 2 OR rcm.fn_FolderLevel(@parent) < 2
    BEGIN
        SET @msg = CONCAT(N'You cannot delete folder ''', @name, N''': you need Edit on it and on the folder it is in.');
        THROW 50003, @msg, 1;
    END;

    CREATE TABLE #sub (Id uniqueidentifier PRIMARY KEY);
    WITH sub AS
    (
        SELECT Id FROM rcm.Folders WHERE Id = @Id
        UNION ALL
        SELECT f.Id FROM rcm.Folders f JOIN sub ON f.ParentId = sub.Id
    )
    INSERT #sub (Id) SELECT Id FROM sub;

    IF EXISTS (SELECT 1 FROM #sub s LEFT JOIN rcm.fn_MyFolderLevels() lv ON lv.FolderId = s.Id WHERE ISNULL(lv.Lvl, 0) < 2)
    BEGIN
        SET @msg = CONCAT(N'You cannot delete folder ''', @name, N''': it holds folders you are not allowed to change.');
        THROW 50003, @msg, 1;
    END;

    DELETE ues FROM rcm.UserEntrySettings ues
    WHERE ues.TargetId IN (SELECT Id FROM #sub)
       OR ues.TargetId IN (SELECT e.Id FROM rcm.Entries e JOIN #sub s ON s.Id = e.FolderId);
    DELETE e FROM rcm.Entries e JOIN #sub s ON s.Id = e.FolderId;
    DELETE f FROM rcm.Folders f JOIN #sub s ON s.Id = f.Id;
    INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName) VALUES (N'delete', N'folder', CONVERT(nvarchar(64), @Id), @name);
    COMMIT;
END
GO

CREATE PROCEDURE rcm.SaveEntry
    @Id uniqueidentifier,
    @FolderId uniqueidentifier,
    @Kind nvarchar(32),
    @Name nvarchar(256),
    @SortOrder int,
    @Body nvarchar(max),
    @ExpectedVersion binary(8) = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @msg nvarchar(2048);
    DECLARE @exists bit = 0, @curFolder uniqueidentifier, @curVersion binary(8), @curName nvarchar(256), @by nvarchar(256), @at datetime2(0);

    BEGIN TRAN;
    SELECT @exists = 1, @curFolder = FolderId, @curVersion = RowVersion, @curName = Name, @by = ModifiedBy, @at = ModifiedUtc
    FROM rcm.Entries WITH (UPDLOCK, HOLDLOCK)
    WHERE Id = @Id;

    IF @FolderId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM rcm.Folders WHERE Id = @FolderId)
    BEGIN
        SET @msg = CONCAT(N'The folder that ''', @Name, N''' goes in was deleted by someone else.');
        THROW 50001, @msg, 1;
    END;

    IF @exists = 0
    BEGIN
        IF @ExpectedVersion IS NOT NULL
        BEGIN
            SET @msg = CONCAT(N'''', @Name, N''' was deleted by someone else.');
            THROW 50001, @msg, 1;
        END;
        IF rcm.fn_FolderLevel(@FolderId) < 2
        BEGIN
            SET @msg = CASE WHEN @FolderId IS NULL THEN N'Only an RCM admin can add entries at the top level.'
                            ELSE CONCAT(N'You cannot add ''', @Name, N''' there: you need Edit on the folder.') END;
            THROW 50003, @msg, 1;
        END;
        INSERT rcm.Entries (Id, FolderId, Kind, Name, SortOrder, Body) VALUES (@Id, @FolderId, @Kind, @Name, @SortOrder, @Body);
        INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName) VALUES (N'add', N'entry', CONVERT(nvarchar(64), @Id), @Name);
    END
    ELSE
    BEGIN
        IF @ExpectedVersion IS NULL OR @ExpectedVersion <> @curVersion
        BEGIN
            SET @msg = CONCAT(N'''', @curName, N''' was changed by ', @by, N' at ', FORMAT(@at, N'yyyy-MM-dd HH:mm'), N' UTC.');
            THROW 50001, @msg, 1;
        END;
        IF rcm.fn_FolderLevel(@curFolder) < 2 OR rcm.fn_FolderLevel(@FolderId) < 2
        BEGIN
            SET @msg = CONCAT(N'You cannot change ''', @curName, N''': you need Edit on its folder.');
            THROW 50003, @msg, 1;
        END;
        UPDATE rcm.Entries
        SET FolderId = @FolderId, Kind = @Kind, Name = @Name, SortOrder = @SortOrder, Body = @Body,
            ModifiedBy = SUSER_SNAME(), ModifiedUtc = SYSUTCDATETIME()
        WHERE Id = @Id;
        INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName) VALUES (N'update', N'entry', CONVERT(nvarchar(64), @Id), @Name);
    END;

    SELECT RowVersion FROM rcm.Entries WHERE Id = @Id;
    COMMIT;
END
GO

CREATE PROCEDURE rcm.DeleteEntry
    @Id uniqueidentifier,
    @ExpectedVersion binary(8)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @msg nvarchar(2048);
    DECLARE @exists bit = 0, @folder uniqueidentifier, @curVersion binary(8), @name nvarchar(256), @by nvarchar(256), @at datetime2(0);

    BEGIN TRAN;
    SELECT @exists = 1, @folder = FolderId, @curVersion = RowVersion, @name = Name, @by = ModifiedBy, @at = ModifiedUtc
    FROM rcm.Entries WITH (UPDLOCK, HOLDLOCK)
    WHERE Id = @Id;

    IF @exists = 0
    BEGIN
        COMMIT;
        RETURN;
    END;
    IF @ExpectedVersion IS NULL OR @ExpectedVersion <> @curVersion
    BEGIN
        SET @msg = CONCAT(N'''', @name, N''' was changed by ', @by, N' at ', FORMAT(@at, N'yyyy-MM-dd HH:mm'), N' UTC.');
        THROW 50001, @msg, 1;
    END;
    IF rcm.fn_FolderLevel(@folder) < 2
    BEGIN
        SET @msg = CONCAT(N'You cannot delete ''', @name, N''': you need Edit on its folder.');
        THROW 50003, @msg, 1;
    END;
    DELETE rcm.UserEntrySettings WHERE TargetId = @Id;
    DELETE rcm.Entries WHERE Id = @Id;
    INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName) VALUES (N'delete', N'entry', CONVERT(nvarchar(64), @Id), @name);
    COMMIT;
END
GO

-- ---------- users and groups (admins) ----------

CREATE PROCEDURE rcm.ListUsers
AS
BEGIN
    SET NOCOUNT ON;
    EXEC rcm.RequireAdmin;
    SELECT u.Id, u.Login, u.DisplayName, u.IsAdmin, u.LastSeenUtc,
           (SELECT STRING_AGG(g.Name, N', ') WITHIN GROUP (ORDER BY g.Name)
            FROM rcm.GroupMembers gm JOIN rcm.Groups g ON g.Id = gm.GroupId
            WHERE gm.UserId = u.Id) AS Groups
    FROM rcm.Users u
    ORDER BY u.Login;
END
GO

-- Adds (Id NULL) or updates a user. A login can be added before the person first opens RCM.
CREATE PROCEDURE rcm.SaveUser
    @Id int = NULL,
    @Login nvarchar(256),
    @DisplayName nvarchar(256),
    @IsAdmin bit
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC rcm.RequireAdmin;
    SET @Login = LTRIM(RTRIM(@Login));
    IF @Login = N'' THROW 50004, N'Enter a login, for example DOMAIN\name or name@domain.com.', 1;

    BEGIN TRAN;
    IF @Id IS NULL
    BEGIN
        INSERT rcm.Users (Login, DisplayName, IsAdmin) VALUES (@Login, NULLIF(@DisplayName, N''), @IsAdmin);
        SET @Id = SCOPE_IDENTITY();
    END
    ELSE
        UPDATE rcm.Users SET Login = @Login, DisplayName = NULLIF(@DisplayName, N''), IsAdmin = @IsAdmin WHERE Id = @Id;

    IF NOT EXISTS (SELECT 1 FROM rcm.Users WHERE IsAdmin = 1) AND IS_MEMBER(N'db_owner') = 0
        THROW 50004, N'RCM needs at least one admin.', 1;
    INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName) VALUES (N'save', N'user', CONVERT(nvarchar(64), @Id), @Login);
    COMMIT;
    SELECT @Id;
END
GO

CREATE PROCEDURE rcm.DeleteUser
    @Id int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC rcm.RequireAdmin;
    BEGIN TRAN;
    DECLARE @login nvarchar(256) = (SELECT Login FROM rcm.Users WHERE Id = @Id);
    DELETE rcm.Users WHERE Id = @Id;
    IF NOT EXISTS (SELECT 1 FROM rcm.Users WHERE IsAdmin = 1) AND IS_MEMBER(N'db_owner') = 0
        THROW 50004, N'RCM needs at least one admin.', 1;
    INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName) VALUES (N'delete', N'user', CONVERT(nvarchar(64), @Id), @login);
    COMMIT;
END
GO

-- Any user may list groups (folder managers pick from them); members are admin only.
CREATE PROCEDURE rcm.ListGroups
AS
BEGIN
    SET NOCOUNT ON;
    SELECT g.Id, g.Name, g.Description, (SELECT COUNT(*) FROM rcm.GroupMembers gm WHERE gm.GroupId = g.Id) AS MemberCount
    FROM rcm.Groups g
    ORDER BY g.Name;
END
GO

CREATE PROCEDURE rcm.SaveGroup
    @Id int = NULL,
    @Name nvarchar(128),
    @Description nvarchar(1000)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC rcm.RequireAdmin;
    SET @Name = LTRIM(RTRIM(@Name));
    IF @Name = N'' THROW 50004, N'Enter a group name.', 1;
    IF EXISTS (SELECT 1 FROM rcm.Groups WHERE Name = @Name AND (@Id IS NULL OR Id <> @Id))
        THROW 50004, N'There is already a group with that name.', 1;

    IF @Id IS NULL
    BEGIN
        INSERT rcm.Groups (Name, Description) VALUES (@Name, NULLIF(@Description, N''));
        SET @Id = SCOPE_IDENTITY();
    END
    ELSE
        UPDATE rcm.Groups SET Name = @Name, Description = NULLIF(@Description, N'') WHERE Id = @Id;
    INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName) VALUES (N'save', N'group', CONVERT(nvarchar(64), @Id), @Name);
    SELECT @Id;
END
GO

CREATE PROCEDURE rcm.DeleteGroup
    @Id int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC rcm.RequireAdmin;
    DECLARE @name nvarchar(128) = (SELECT Name FROM rcm.Groups WHERE Id = @Id);
    DELETE rcm.Groups WHERE Id = @Id;
    INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName) VALUES (N'delete', N'group', CONVERT(nvarchar(64), @Id), @name);
END
GO

CREATE PROCEDURE rcm.GetGroupMembers
    @GroupId int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC rcm.RequireAdmin;
    SELECT UserId FROM rcm.GroupMembers WHERE GroupId = @GroupId;
END
GO

-- @UserIds is a JSON array of user ids, for example [1,4,7]. Replaces the membership.
CREATE PROCEDURE rcm.SetGroupMembers
    @GroupId int,
    @UserIds nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC rcm.RequireAdmin;
    BEGIN TRAN;
    DELETE rcm.GroupMembers WHERE GroupId = @GroupId;
    INSERT rcm.GroupMembers (GroupId, UserId)
    SELECT DISTINCT @GroupId, u.Id
    FROM OPENJSON(@UserIds) j
    JOIN rcm.Users u ON u.Id = TRY_CONVERT(int, j.value);
    INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName)
    VALUES (N'members', N'group', CONVERT(nvarchar(64), @GroupId), (SELECT Name FROM rcm.Groups WHERE Id = @GroupId));
    COMMIT;
END
GO

-- ---------- folder permissions (Manage) ----------

-- Result sets: the folder's BreakInheritance flag; its own permissions; permissions inherited from above.
CREATE PROCEDURE rcm.GetFolderPermissions
    @FolderId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    IF rcm.fn_FolderLevel(@FolderId) < 3
        THROW 50003, N'You need Manage on this folder to see its permissions.', 1;

    DECLARE @break bit = (SELECT BreakInheritance FROM rcm.Folders WHERE Id = @FolderId);
    SELECT @break;

    SELECT fp.GroupId, g.Name AS GroupName, fp.Level
    FROM rcm.FolderPermissions fp JOIN rcm.Groups g ON g.Id = fp.GroupId
    WHERE fp.FolderId = @FolderId
    ORDER BY g.Name;

    -- Walk up until (and including) the first folder that breaks inheritance.
    WITH up AS
    (
        SELECT p.Id, p.ParentId, p.BreakInheritance, p.Name, 1 AS Depth
        FROM rcm.Folders c JOIN rcm.Folders p ON p.Id = c.ParentId
        WHERE c.Id = @FolderId AND @break = 0
        UNION ALL
        SELECT p.Id, p.ParentId, p.BreakInheritance, p.Name, up.Depth + 1
        FROM up JOIN rcm.Folders p ON p.Id = up.ParentId
        WHERE up.BreakInheritance = 0
    )
    SELECT fp.GroupId, g.Name AS GroupName, fp.Level, up.Name AS FromFolder
    FROM up
    JOIN rcm.FolderPermissions fp ON fp.FolderId = up.Id
    JOIN rcm.Groups g ON g.Id = fp.GroupId
    ORDER BY up.Depth, g.Name;
END
GO

-- @Permissions is a JSON array: [{"groupId":1,"level":2}, ...]. Replaces the folder's own permissions.
CREATE PROCEDURE rcm.SetFolderPermissions
    @FolderId uniqueidentifier,
    @BreakInheritance bit,
    @Permissions nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF rcm.fn_FolderLevel(@FolderId) < 3
        THROW 50003, N'You need Manage on this folder to change its permissions.', 1;

    BEGIN TRAN;
    UPDATE rcm.Folders SET BreakInheritance = @BreakInheritance WHERE Id = @FolderId;
    DELETE rcm.FolderPermissions WHERE FolderId = @FolderId;
    INSERT rcm.FolderPermissions (FolderId, GroupId, Level)
    SELECT @FolderId, g.Id, MAX(j.Level)
    FROM OPENJSON(@Permissions) WITH (GroupId int N'$.groupId', Level tinyint N'$.level') j
    JOIN rcm.Groups g ON g.Id = j.GroupId
    WHERE j.Level BETWEEN 1 AND 3
    GROUP BY g.Id;
    INSERT rcm.ChangeLog (Action, TargetKind, TargetId, TargetName)
    VALUES (N'permissions', N'folder', CONVERT(nvarchar(64), @FolderId), (SELECT Name FROM rcm.Folders WHERE Id = @FolderId));
    COMMIT;
END
GO

-- ---------- the caller's own preferences ----------

CREATE PROCEDURE rcm.GetMyPreferences
AS
BEGIN
    SET NOCOUNT ON;
    SELECT p.[Key], p.Value
    FROM rcm.UserPreferences p JOIN rcm.Users u ON u.Id = p.UserId
    WHERE u.Login = SUSER_SNAME();
END
GO

-- A NULL value removes the preference.
CREATE PROCEDURE rcm.SetMyPreference
    @Key nvarchar(128),
    @Value nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @me int = (SELECT Id FROM rcm.Users WHERE Login = SUSER_SNAME());
    IF @me IS NULL THROW 50004, N'Unknown user. Restart RCM to register.', 1;
    BEGIN TRAN;
    DELETE rcm.UserPreferences WHERE UserId = @me AND [Key] = @Key;
    IF @Value IS NOT NULL INSERT rcm.UserPreferences (UserId, [Key], Value) VALUES (@me, @Key, @Value);
    COMMIT;
END
GO

CREATE PROCEDURE rcm.GetMyEntrySettings
AS
BEGIN
    SET NOCOUNT ON;
    SELECT s.TargetId, s.Body
    FROM rcm.UserEntrySettings s JOIN rcm.Users u ON u.Id = s.UserId
    WHERE u.Login = SUSER_SNAME();
END
GO

-- A NULL body removes the setting.
CREATE PROCEDURE rcm.SetMyEntrySetting
    @TargetId uniqueidentifier,
    @Body nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    DECLARE @me int = (SELECT Id FROM rcm.Users WHERE Login = SUSER_SNAME());
    IF @me IS NULL THROW 50004, N'Unknown user. Restart RCM to register.', 1;
    BEGIN TRAN;
    DELETE rcm.UserEntrySettings WHERE UserId = @me AND TargetId = @TargetId;
    IF @Body IS NOT NULL INSERT rcm.UserEntrySettings (UserId, TargetId, Body) VALUES (@me, @TargetId, @Body);
    COMMIT;
END
GO

CREATE PROCEDURE rcm.GetChangeLog
    @Top int = 200
AS
BEGIN
    SET NOCOUNT ON;
    EXEC rcm.RequireAdmin;
    SELECT TOP (@Top) Id, ChangedUtc, ChangedBy, Action, TargetKind, TargetId, TargetName
    FROM rcm.ChangeLog
    ORDER BY Id DESC;
END
GO

-- rcm_user may run the procedures and nothing else: no direct table access.
GRANT EXECUTE ON SCHEMA::rcm TO rcm_user;
GO
