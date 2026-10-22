-- Exports each user's lists and favorites, hidden items and watch progress from a MediaRiffle database as an import
-- script for another one: e.g. moving to a new server whose libraries were scanned afresh, so its shows, movies and
-- files have new ids. Only reads this database.
--
-- The import script doesn't use ids. It finds each user by user name, and each show, movie, artist, album and file by
-- its library's name and what it is (a show's folder, a movie's or album's group key, an artist's name, a file's path
-- in the library). What the new database doesn't have (not scanned yet, or gone) is left out, as is anything already
-- there, so it can be run more than once; watch progress already there is kept unless this one is newer.
--
-- 1. Export, on the old server. DB Browser for SQLite: Open Database -> its mediariffle.db (C:\ProgramData\MediaRiffle,
--    with MediaRiffle stopped, or a copy); Execute SQL -> open this file -> Execute all (F5). The result is one cell:
--    click it, and in the Edit Database Cell pane, Export to file -> import-user-data.sql. Or on the command line:
--      sqlite3 mediariffle.db < export-user-data.sql > import-user-data.sql
--
-- 2. Import, on the new server, its libraries scanned (in a container, copy the file in first:
--    pct push 120 import-user-data.sql /root/import-user-data.sql):
--      apt install -y sqlite3
--      systemctl stop mediariffle
--      cp /var/lib/mediariffle/mediariffle.db /root/mediariffle.db.before-import
--      sqlite3 -bail /var/lib/mediariffle/mediariffle.db < /root/import-user-data.sql
--      chown mediariffle:mediariffle /var/lib/mediariffle/mediariffle.db*
--      systemctl start mediariffle

WITH
-- The SQL that finds each user, and each show, movie, artist, album and file, in the new database
who AS (
    SELECT Id, '(SELECT Id FROM AspNetUsers WHERE NormalizedUserName = ' || quote(NormalizedUserName) || ')' AS find
    FROM AspNetUsers),
thing AS (
    SELECT 'ShowId' AS col, s.Id,
           '(SELECT s.Id FROM Shows s JOIN Libraries l ON l.Id = s.LibraryId WHERE l.Name = ' || quote(l.Name) ||
           ' AND s.Folder = ' || quote(s.Folder) || ')' AS find
    FROM Shows s JOIN Libraries l ON l.Id = s.LibraryId
    UNION ALL
    SELECT 'MovieId', m.Id,
           '(SELECT m.Id FROM Movies m JOIN Libraries l ON l.Id = m.LibraryId WHERE l.Name = ' || quote(l.Name) ||
           ' AND m.GroupKey = ' || quote(m.GroupKey) || ')'
    FROM Movies m JOIN Libraries l ON l.Id = m.LibraryId
    UNION ALL
    SELECT 'ArtistId', a.Id,
           '(SELECT a.Id FROM Artists a JOIN Libraries l ON l.Id = a.LibraryId WHERE l.Name = ' || quote(l.Name) ||
           ' AND a.Name = ' || quote(a.Name) || ')'
    FROM Artists a JOIN Libraries l ON l.Id = a.LibraryId
    UNION ALL
    SELECT 'AlbumId', a.Id,
           '(SELECT a.Id FROM Albums a JOIN Libraries l ON l.Id = a.LibraryId WHERE l.Name = ' || quote(l.Name) ||
           ' AND a.GroupKey = ' || quote(a.GroupKey) || ')'
    FROM Albums a JOIN Libraries l ON l.Id = a.LibraryId
    UNION ALL
    SELECT 'MediaFileId', f.Id,
           '(SELECT f.Id FROM MediaFiles f JOIN Libraries l ON l.Id = f.LibraryId WHERE l.Name = ' || quote(l.Name) ||
           ' AND f.RelativePath = ' || quote(f.RelativePath) || ')'
    FROM MediaFiles f JOIN Libraries l ON l.Id = f.LibraryId),
-- What a list entry or hidden item is of: exactly one of its ids is set
item AS (
    SELECT 'tag' AS kind, t.UserId, t.Tag, t.CreatedUtc,
           CASE WHEN t.ShowId IS NOT NULL THEN 'ShowId' WHEN t.MovieId IS NOT NULL THEN 'MovieId'
                WHEN t.ArtistId IS NOT NULL THEN 'ArtistId' WHEN t.AlbumId IS NOT NULL THEN 'AlbumId' ELSE 'MediaFileId' END AS col,
           coalesce(t.ShowId, t.MovieId, t.ArtistId, t.AlbumId, t.MediaFileId) AS ThingId
    FROM UserTags t
    UNION ALL
    SELECT 'hidden', h.UserId, NULL, h.CreatedUtc,
           CASE WHEN h.ShowId IS NOT NULL THEN 'ShowId' WHEN h.MovieId IS NOT NULL THEN 'MovieId'
                WHEN h.ArtistId IS NOT NULL THEN 'ArtistId' WHEN h.AlbumId IS NOT NULL THEN 'AlbumId' ELSE 'MediaFileId' END,
           coalesce(h.ShowId, h.MovieId, h.ArtistId, h.AlbumId, h.MediaFileId)
    FROM Hidden h),
statement AS (
    SELECT 1 AS part, CASE i.kind
        WHEN 'tag' THEN
            'INSERT INTO UserTags (UserId, Tag, ' || i.col || ', CreatedUtc) SELECT u, ' || quote(i.Tag) || ', i, ' ||
            quote(i.CreatedUtc) || ' FROM (SELECT ' || w.find || ' AS u, ' || x.find || ' AS i) WHERE u IS NOT NULL AND ' ||
            'i IS NOT NULL AND NOT EXISTS (SELECT 1 FROM UserTags e WHERE e.UserId = u AND e.Tag = ' || quote(i.Tag) ||
            ' AND e.' || i.col || ' = i);'
        ELSE
            'INSERT INTO Hidden (UserId, ' || i.col || ', CreatedUtc) SELECT u, i, ' || quote(i.CreatedUtc) || ' FROM (SELECT ' ||
            w.find || ' AS u, ' || x.find || ' AS i) WHERE u IS NOT NULL AND i IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ' ||
            'Hidden e WHERE e.UserId = u AND e.' || i.col || ' = i);'
        END AS sql
    FROM item i JOIN who w ON w.Id = i.UserId JOIN thing x ON x.col = i.col AND x.Id = i.ThingId
    UNION ALL
    SELECT 2, 'INSERT INTO Progress (UserId, MediaFileId, PositionSeconds, Finished, LeftContinue, UpdatedUtc) SELECT u, i, ' ||
           quote(p.PositionSeconds) || ', ' || quote(p.Finished) || ', ' || quote(p.LeftContinue) || ', ' ||
           quote(p.UpdatedUtc) || ' FROM (SELECT ' || w.find || ' AS u, ' || x.find || ' AS i) WHERE u IS NOT NULL AND ' ||
           'i IS NOT NULL ON CONFLICT (UserId, MediaFileId) DO UPDATE SET PositionSeconds = excluded.PositionSeconds, ' ||
           'Finished = excluded.Finished, LeftContinue = excluded.LeftContinue, UpdatedUtc = excluded.UpdatedUtc ' ||
           'WHERE excluded.UpdatedUtc > Progress.UpdatedUtc;'
    FROM Progress p JOIN who w ON w.Id = p.UserId JOIN thing x ON x.col = 'MediaFileId' AND x.Id = p.MediaFileId)
SELECT
    '-- MediaRiffle lists and favorites, hidden items and watch progress, exported ' || datetime('now') || ' UTC' || char(10) ||
    '-- from ' || (SELECT COUNT(*) FROM AspNetUsers) || ' users: ' || (SELECT COUNT(*) FROM UserTags) || ' list entries, ' ||
    (SELECT COUNT(*) FROM Hidden) || ' hidden, ' || (SELECT COUNT(*) FROM Progress) || ' watch progress.' || char(10) ||
    '-- Run against the new database with sqlite3 (see deploy/export-user-data.sql).' || char(10) ||
    'BEGIN;' || char(10) ||
    coalesce((SELECT group_concat(sql, char(10)) FROM (SELECT sql FROM statement ORDER BY part)), '') || char(10) ||
    'SELECT ''Added or updated '' || total_changes() || '' of ' ||
    ((SELECT COUNT(*) FROM UserTags) + (SELECT COUNT(*) FROM Hidden) + (SELECT COUNT(*) FROM Progress)) ||
    ' (fewer when some aren''''t in the new libraries yet, or were there already)'';' || char(10) ||
    'COMMIT;' AS ImportScript;
