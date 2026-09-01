CREATE PROCEDURE %SCHEMA%.uspMarkIncomingOwnership
    @IDLIST %SCHEMA%.EnvelopeIdList READONLY,
    @owner INT
AS

-- GH-4216: without the status predicate this also rewrites rows that are already Incoming or Handled.
-- The load side of the poll selects only Scheduled rows, so constraining the update to match costs nothing
-- and keeps the promotion from touching a row it never selected.
UPDATE %SCHEMA%.wolverine_incoming_envelopes
SET owner_id = @owner, status = 'Incoming'
WHERE status = 'Scheduled' AND id IN (SELECT ID FROM @IDLIST);
