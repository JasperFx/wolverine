CREATE PROCEDURE %SCHEMA%.uspMarkOutgoingOwnership
    @IDLIST %SCHEMA%.EnvelopeIdList READONLY,
    @owner INT
AS

UPDATE %SCHEMA%.wolverine_outgoing_envelopes
SET owner_id = @owner
WHERE id IN (SELECT ID FROM @IDLIST);
