import {BUNDLED_LANGUAGES} from 'shiki'
//import {withMermaid} from "vitepress-plugin-mermaid"

// Include `cs` as alias for csharp
BUNDLED_LANGUAGES
    .find(lang => lang.id === 'csharp').aliases.push('cs');

//export default withMermaid({
export default {
    base: '/',
    lang: 'en-US',
    title: 'Wolverine',
    description: 'Next Generation Command and Message Bus for .NET',
    head: [
        ['link', {rel: 'apple-touch-icon', type: 'image/png', size: "180x180", href: '/apple-icon-180x180.png'}],
        ['link', {rel: 'icon', type: 'image/png', size: "32x32", href: '/favicon-32x32.png'}],
        ['link', {rel: 'icon', type: 'image/png', size: "16x16", href: '/favicon-16x16.png'}],
        ['link', {rel: 'manifest', manifest: '/manifest.json'}],
    ],
    lastUpdated: true,
    themeConfig: {
        logo: '/logo.png',

        nav: [
            {text: 'Guide', link: '/guide/basics'},
            {text: 'Tutorials', link: '/tutorials/'},
            {
                text: 'Discord | Join Chat',
                link: 'https://discord.gg/WMxrvegf8H'
            }
        ],

        algolia: {
            appId: 'IS2ZRHIXW9',
            apiKey: 'c8a9f5cb4e0f80733d0dadb4ae8d06ad',
            indexName: 'wolverine_index'
        },

        editLink: {
            pattern: 'https://github.com/JasperFx/wolverine/edit/main/docs/:path',
            text: 'Suggest changes to this page'
        },

        socialLinks: [
            { 
                icon: 'github', 
                link: 'https://github.com/JasperFx/wolverine' 
            },
        ],

        footer: {
            message: 'Released under the MIT License.',
            copyright: 'Copyright © Jeremy D. Miller and contributors.',
        },

        sidebar: {
            '/': [
                {
                    text: 'Tutorials',
                    collapsible: true,
                    items: [
                        {text: 'Getting Started', link: '/tutorials/getting-started'},
                        {text: 'Wolverine as Mediator', link: '/tutorials/mediator'},
                        {text: 'Best Practices', link: '/tutorials/best-practices'},
                        {text: 'Ping/Pong Messaging', link: '/tutorials/ping-pong'},
                        {text: 'Custom Middleware', link: '/tutorials/middleware'},
                        {text: 'Wolverine and Serverless', link: '/tutorials/serverless'}
                    ]
                },
                {
                    text: 'General',
                    collapsible: true,
                    collapsed: true,
                    items: [
                        {text: 'Basic Concepts', link: '/guide/basics'},
                        {text: 'Configuration', link: '/guide/configuration'},
                        {text: 'Runtime Architecture', link: '/guide/runtime'},
                        {text: 'Instrumentation and Metrics', link: '/guide/logging'},
                        {text: 'Diagnostics', link: '/guide/diagnostics'},
                        {text: 'Test Automation Support', link: '/guide/testing'},
                        {text: 'Command Line Integration', link: '/guide/command-line'},
                        {text: 'Code Generation', link: '/guide/codegen'},
                        {text: 'Extensions', link: '/guide/extensions'},
                        {text: 'Sample Projects', link: '/guide/samples'}
                    ]
                },
                {
                    text: 'Messages and Handlers',
                    collapsible: true,
                    collapsed: true,
                    items: [
                        {text: 'Messages and Serialization', link: '/guide/messages'},
                        {
                            text: 'Message Handlers', link: '/guide/handlers/', items: [
                                {text: 'Discovery', link: '/guide/handlers/discovery'},
                                {text: 'Error Handling', link: '/guide/handlers/error-handling'},
                                {text: 'Return Values', link: '/guide/handlers/return-values'},
                                {text: 'Cascading Messages', link: '/guide/handlers/cascading'},
                                {text: 'Side Effects', link: '/guide/handlers/side-effects'},
                                {text: 'Middleware', link: '/guide/handlers/middleware'},
                                {text: 'Multi-Tenancy', link: '/guide/handlers/multi-tenancy'},
                                {text: 'Execution Timeouts', link: '/guide/handlers/timeout'},
                                {text: 'Fluent Validation Middleware', link: '/guide/handlers/fluent-validation'}
                            ]
                        },
                    ]
                },
                {
                    text: 'Messaging',
                    collapsible: true,
                    collapsed: true,
                    items: [
                        {text: 'Introduction to Messaging', link: '/guide/messaging/introduction'},
                        {text: 'Sending Messages', link: '/guide/messaging/message-bus'},
                        {text: 'Subscriptions & Message Routing', link: '/guide/messaging/subscriptions'},
                        {text: 'Message Listeners', link: '/guide/messaging/listeners'},
                        {
                            text: 'Transports',
                            collapsible: true,
                            collapsed: true,
                            items: [
                                {text: 'Local Queues', link: '/guide/messaging/transports/local'},
                                {text: 'Rabbit MQ', link: '/guide/messaging/transports/rabbitmq/', items:[
                                        {text: 'Publishing', link:'/guide/messaging/transports/rabbitmq/publishing'},
                                        {text: 'Listening', link:'/guide/messaging/transports/rabbitmq/listening'},
                                        {text: 'Dead Letter Queues', link:'/guide/messaging/transports/rabbitmq/deadletterqueues'},
                                        {text: 'Conventional Routing', link:'/guide/messaging/transports/rabbitmq/conventional-routing'},
                                        {text: 'Queue, Topic, and Binding Management', link:'/guide/messaging/transports/rabbitmq/object-management'},
                                        {text: 'Topics', link:'/guide/messaging/transports/rabbitmq/topics'},
                                        {text: 'Interoperability', link:'/guide/messaging/transports/rabbitmq/interoperability'}
                                    ]},
                                {text: 'Azure Service Bus', link: '/guide/messaging/transports/azureservicebus/', items:[
                                        {text: 'Publishing', link:'/guide/messaging/transports/azureservicebus/publishing'},
                                        {text: 'Listening', link:'/guide/messaging/transports/azureservicebus/listening'},
                                        {text: 'Dead Letter Queues', link:'/guide/messaging/transports/azureservicebus/deadletterqueues'},
                                        {text: 'Conventional Routing', link:'/guide/messaging/transports/azureservicebus/conventional-routing'},
                                        {text: 'Queues', link:'/guide/messaging/transports/azureservicebus/object-management'},
                                        {text: 'Topics and Subscriptions', link:'/guide/messaging/transports/azureservicebus/topics'},
                                        {text: 'Interoperability', link:'/guide/messaging/transports/azureservicebus/interoperability'},
                                        {text: 'Session Identifiers and FIFO Queues', link: '/guide/messaging/transports/azureservicebus/session-identifiers'},
                                        {text: 'Scheduled Delivery', link: '/guide/messaging/transports/azureservicebus/scheduled'}
                                    ]},
                                {text: 'Amazon SQS', link: '/guide/messaging/transports/sqs/', items:[
                                        {text: 'Publishing', link:'/guide/messaging/transports/sqs/publishing'},
                                        {text: 'Listening', link:'/guide/messaging/transports/sqs/listening'},
                                        {text: 'Dead Letter Queues', link:'/guide/messaging/transports/sqs/deadletterqueues'},
                                        {text: 'Configuring Queues', link:'/guide/messaging/transports/sqs/queues'},
                                        {text: 'Conventional Routing', link:'/guide/messaging/transports/sqs/conventional-routing'},
                                        {text: 'Interoperability', link:'/guide/messaging/transports/sqs/interoperability'}
                                    ]},
                                {text: 'TCP', link: '/guide/messaging/transports/tcp'},
                                {text: 'Sql Server', link: '/guide/messaging/transports/sqlserver'},
                                {text: 'MQTT', link: '/guide/messaging/transports/mqtt'}
                            ]
                        },
                        {text: 'Endpoint Specific Operations', link: '/guide/messaging/endpoint-operations'},
                        {text: 'Broadcast to a Specific Topic', link: '/guide/messaging/broadcast-to-topic'},
                        {text: 'Message Expiration', link: '/guide/messaging/expiration'},
                        {text: 'Endpoint Policies', link: '/guide/messaging/policies'}
                    ]
                },
                {
                    text: 'ASP.Net Core Integration',
                    collapsible: true,
                    collapsed: true,
                    items: [
                        {text: 'Http Services with Wolverine', link: '/guide/http/'},
                        {text: 'Integration with ASP.Net Core', link: '/guide/http/integration'},
                        {text: 'Endpoints', link: '/guide/http/endpoints'},
                        {text: 'Json', link: '/guide/http/json'},
                        {text: 'Routing', link: '/guide/http/routing'},
                        {text: 'Authentication and Authorization', link: '/guide/http/security'},
                        {text: 'Working with Querystring', link: '/guide/http/querystring'},
                        {text: 'Headers', link: '/guide/http/headers'},
                        {text: 'Middleware', link: '/guide/http/middleware.md'},
                        {text: 'Policies', link: '/guide/http/policies.md'},
                        {text: 'OpenAPI Metadata', link: '/guide/http/metadata'},
                        {text: 'Using as Mediator', link: '/guide/http/mediator'},
                        {text: 'Multi-Tenancy and ASP.Net Core', link: '/guide/http/multi-tenancy'},
                        {text: 'Publishing Messages', link: '/guide/http/messaging'},
                        {text: 'Integration with Sagas', link: '/guide/http/sagas'},
                        {text: 'Integration with Marten', link: '/guide/http/marten'},
                        {text: 'Fluent Validation', link: '/guide/http/fluentvalidation'},
                        {text: 'Problem Details', link: '/guide/http/problemdetails'},
                    ]
                },
                {
                    text: 'Durability and Persistence',
                    collapsible: true,
                    collapsed: true,
                    items: [
                        {text: 'Durable Inbox and Outbox Messaging', link: '/guide/durability/'},
                        {text: 'Sagas', link: '/guide/durability/sagas'},
                        {text: 'Marten Integration', link: '/guide/durability/marten/', collapsible: true, collapsed: false, items: [
                                {text: 'Operation Side Effects', link: '/guide/durability/marten/operations'},
                                {text: 'Aggregate Handlers and Event Sourcing', link: '/guide/durability/marten/event-sourcing'},
                                {text: 'Multi-Tenancy and Marten', link: '/guide/durability/marten/multi-tenancy'}
                            ]},
                        {text: 'Sql Server Integration', link: '/guide/durability/sqlserver'},
                        {text: 'Entity Framework Core Integration', link: '/guide/durability/efcore'},
                        {text: 'Managing Message Storage', link: '/guide/durability/managing'},
                        {text: 'Dead Letter Storage', link: '/guide/durability/dead-letter-storage'},
                        {text: 'Idempotent Message Delivery', link:'/guide/durability/idempotency'}
                    ]
                },

            ]
        }
    },
    markdown: {
        linkify: false
    },
    ignoreDeadLinks: true
}

