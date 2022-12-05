create table %SCHEMA%.wolverine_outgoing_envelopes
(
    id
    uniqueidentifier
    not
    null
    primary
    key,
    owner_id
    int
    not
    null,
    destination
    varchar
(
    250
) not null,
    deliver_by datetimeoffset,
    body varbinary
(
    max
) not null
    );

create table %SCHEMA%.wolverine_incoming_envelopes
(
    id
    uniqueidentifier
    not
    null
    primary
    key,
    status
    varchar
(
    25
) not null,
    owner_id int not null,
    execution_time datetimeoffset default NULL,
    attempts int default 0 not null,
    body varbinary
(
    max
) not null
    );

create table %SCHEMA%.wolverine_dead_letters
(
    id
    uniqueidentifier
    not
    null
    primary
    key,

    source
    VARCHAR
(
    250
),
    message_type VARCHAR
(
    250
),
    explanation VARCHAR
(
    250
),
    exception_text VARCHAR
(
    MAX
),
    exception_type VARCHAR
(
    250
),
    exception_message VARCHAR
(
    MAX
),

    body varbinary
(
    max
) not null
    );

IF
NOT EXISTS(SELECT * FROM sys.table_types t INNER JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.name = 'EnvelopeIdList' AND s.name = '%SCHEMA%' )
BEGIN
CREATE TYPE %SCHEMA%.EnvelopeIdList AS TABLE (ID UNIQUEIDENTIFIER)
END








