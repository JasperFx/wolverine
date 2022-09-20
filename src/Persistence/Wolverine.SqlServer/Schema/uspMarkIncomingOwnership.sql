CREATE PROCEDURE %SCHEMA%.uspMarkIncomingOwnership
    @IDLIST %SCHEMA%.EnvelopeIdList READONLY,
    @owner INT
AS

UPDATE %SCHEMA%.wolverine_incoming_envelopes
SET owner_id = @owner, status = 'Incoming'
WHERE id IN (SELECT ID FROM @IDLIST);
