-- Carries each user's lists and favorites, hidden items and watch progress from an old MediaRiffle database into a new
-- one whose libraries were scanned afresh (so its shows, movies and files have new ids): e.g. after moving to another
-- server, or after a library's folder was changed under Admin -> Libraries, which starts it afresh.
--
-- Things are matched by what they are, not by id: users by user name, libraries by name, shows by folder, movies and
-- albums by their group key, artists by name, files by their path in the library. What the new database doesn't have
-- (not scanned yet, or gone) is left out and counted. Running it again adds nothing twice; watch progress already in
-- the new database is kept unless the old one is newer.
--
-- With MediaRiffle stopped, the new database backed up, and the old one (with its -wal and -shm files, if any) at
-- /root/old/mediariffle.db:
--
--   systemctl stop mediariffle
--   cp /var/lib/mediariffle/mediariffle.db /root/mediariffle.db.before-carry
--   sqlite3 /var/lib/mediariffle/mediariffle.db -cmd "ATTACH '/root/old/mediariffle.db' AS old" < carry-user-data.sql
--   systemctl start mediariffle

.bail on
.headers off
.mode list

BEGIN;

CREATE TEMP TABLE map_user AS
    SELECT o.Id AS OldId, n.Id AS NewId FROM old.AspNetUsers o JOIN main.AspNetUsers n ON n.NormalizedUserName = o.NormalizedUserName;
CREATE TEMP TABLE map_library AS
    SELECT o.Id AS OldId, n.Id AS NewId FROM old.Libraries o JOIN main.Libraries n ON n.Name = o.Name;
CREATE TEMP TABLE map_show AS
    SELECT o.Id AS OldId, n.Id AS NewId FROM old.Shows o
    JOIN temp.map_library l ON l.OldId = o.LibraryId JOIN main.Shows n ON n.LibraryId = l.NewId AND n.Folder = o.Folder;
CREATE TEMP TABLE map_movie AS
    SELECT o.Id AS OldId, n.Id AS NewId FROM old.Movies o
    JOIN temp.map_library l ON l.OldId = o.LibraryId JOIN main.Movies n ON n.LibraryId = l.NewId AND n.GroupKey = o.GroupKey;
CREATE TEMP TABLE map_artist AS
    SELECT o.Id AS OldId, n.Id AS NewId FROM old.Artists o
    JOIN temp.map_library l ON l.OldId = o.LibraryId JOIN main.Artists n ON n.LibraryId = l.NewId AND n.Name = o.Name;
CREATE TEMP TABLE map_album AS
    SELECT o.Id AS OldId, n.Id AS NewId FROM old.Albums o
    JOIN temp.map_library l ON l.OldId = o.LibraryId JOIN main.Albums n ON n.LibraryId = l.NewId AND n.GroupKey = o.GroupKey;
CREATE TEMP TABLE map_file AS
    SELECT o.Id AS OldId, n.Id AS NewId FROM old.MediaFiles o
    JOIN temp.map_library l ON l.OldId = o.LibraryId JOIN main.MediaFiles n ON n.LibraryId = l.NewId AND n.RelativePath = o.RelativePath;

SELECT 'Users matched: ' || (SELECT COUNT(*) FROM temp.map_user) || ' of ' || (SELECT COUNT(*) FROM old.AspNetUsers);
SELECT 'Libraries matched: ' || (SELECT COUNT(*) FROM temp.map_library) || ' of ' || (SELECT COUNT(*) FROM old.Libraries);

-- An old row with each of its ids mapped (an id it doesn't have stays null); rows with an id that has no match are left out
CREATE TEMP VIEW old_tags AS
    SELECT u.NewId AS UserId, t.Tag, s.NewId AS ShowId, m.NewId AS MovieId, ar.NewId AS ArtistId, al.NewId AS AlbumId,
           f.NewId AS MediaFileId, t.CreatedUtc,
           (t.ShowId IS NULL OR s.NewId IS NOT NULL) AND (t.MovieId IS NULL OR m.NewId IS NOT NULL) AND
           (t.ArtistId IS NULL OR ar.NewId IS NOT NULL) AND (t.AlbumId IS NULL OR al.NewId IS NOT NULL) AND
           (t.MediaFileId IS NULL OR f.NewId IS NOT NULL) AS Found
    FROM old.UserTags t JOIN temp.map_user u ON u.OldId = t.UserId
    LEFT JOIN temp.map_show s ON s.OldId = t.ShowId LEFT JOIN temp.map_movie m ON m.OldId = t.MovieId
    LEFT JOIN temp.map_artist ar ON ar.OldId = t.ArtistId LEFT JOIN temp.map_album al ON al.OldId = t.AlbumId
    LEFT JOIN temp.map_file f ON f.OldId = t.MediaFileId;

INSERT INTO main.UserTags (UserId, Tag, ShowId, MovieId, ArtistId, AlbumId, MediaFileId, CreatedUtc)
    SELECT o.UserId, o.Tag, o.ShowId, o.MovieId, o.ArtistId, o.AlbumId, o.MediaFileId, o.CreatedUtc FROM temp.old_tags o
    WHERE o.Found AND NOT EXISTS (SELECT 1 FROM main.UserTags x WHERE x.UserId = o.UserId AND x.Tag = o.Tag
        AND x.ShowId IS o.ShowId AND x.MovieId IS o.MovieId AND x.ArtistId IS o.ArtistId AND x.AlbumId IS o.AlbumId
        AND x.MediaFileId IS o.MediaFileId);
SELECT 'Lists and favorites: ' || changes() || ' added, ' || (SELECT COUNT(*) FROM temp.old_tags WHERE NOT Found) ||
       ' not in the new libraries';

CREATE TEMP VIEW old_hidden AS
    SELECT u.NewId AS UserId, s.NewId AS ShowId, m.NewId AS MovieId, ar.NewId AS ArtistId, al.NewId AS AlbumId,
           f.NewId AS MediaFileId, h.CreatedUtc,
           (h.ShowId IS NULL OR s.NewId IS NOT NULL) AND (h.MovieId IS NULL OR m.NewId IS NOT NULL) AND
           (h.ArtistId IS NULL OR ar.NewId IS NOT NULL) AND (h.AlbumId IS NULL OR al.NewId IS NOT NULL) AND
           (h.MediaFileId IS NULL OR f.NewId IS NOT NULL) AS Found
    FROM old.Hidden h JOIN temp.map_user u ON u.OldId = h.UserId
    LEFT JOIN temp.map_show s ON s.OldId = h.ShowId LEFT JOIN temp.map_movie m ON m.OldId = h.MovieId
    LEFT JOIN temp.map_artist ar ON ar.OldId = h.ArtistId LEFT JOIN temp.map_album al ON al.OldId = h.AlbumId
    LEFT JOIN temp.map_file f ON f.OldId = h.MediaFileId;

INSERT INTO main.Hidden (UserId, ShowId, MovieId, ArtistId, AlbumId, MediaFileId, CreatedUtc)
    SELECT o.UserId, o.ShowId, o.MovieId, o.ArtistId, o.AlbumId, o.MediaFileId, o.CreatedUtc FROM temp.old_hidden o
    WHERE o.Found AND NOT EXISTS (SELECT 1 FROM main.Hidden x WHERE x.UserId = o.UserId AND x.ShowId IS o.ShowId
        AND x.MovieId IS o.MovieId AND x.ArtistId IS o.ArtistId AND x.AlbumId IS o.AlbumId AND x.MediaFileId IS o.MediaFileId);
SELECT 'Hidden: ' || changes() || ' added, ' || (SELECT COUNT(*) FROM temp.old_hidden WHERE NOT Found) ||
       ' not in the new libraries';

-- Watch progress: one row per user and file; the newer of the two wins
INSERT INTO main.Progress (UserId, MediaFileId, PositionSeconds, Finished, LeftContinue, UpdatedUtc)
    SELECT u.NewId, f.NewId, p.PositionSeconds, p.Finished, p.LeftContinue, p.UpdatedUtc
    FROM old.Progress p JOIN temp.map_user u ON u.OldId = p.UserId JOIN temp.map_file f ON f.OldId = p.MediaFileId
    WHERE true
    ON CONFLICT (UserId, MediaFileId) DO UPDATE SET
        PositionSeconds = excluded.PositionSeconds, Finished = excluded.Finished,
        LeftContinue = excluded.LeftContinue, UpdatedUtc = excluded.UpdatedUtc
    WHERE excluded.UpdatedUtc > main.Progress.UpdatedUtc;
SELECT 'Watch progress: ' || changes() || ' added or updated, ' ||
       (SELECT COUNT(*) FROM old.Progress p JOIN temp.map_user u ON u.OldId = p.UserId
        WHERE p.MediaFileId NOT IN (SELECT OldId FROM temp.map_file)) || ' not in the new libraries';

COMMIT;
