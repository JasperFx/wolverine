CREATE PROCEDURE %SCHEMA%.uspDeleteOutgoingEnvelopes
    @IDLIST %SCHEMA%.EnvelopeIdList READONLY
AS

DELETE
FROM %SCHEMA%.wolverine_outgoing_envelopes
WHERE id IN (SELECT ID FROM @IDLIST);
