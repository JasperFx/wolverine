import { withMermaid } from "vitepress-plugin-mermaid"
import { defineConfig, type DefaultTheme, type UserConfig } from 'vitepress'
import llmstxt from 'vitepress-plugin-llms'
import blockEmbedPlugin from 'markdown-it-block-embed'

const config: UserConfig<DefaultTheme.Config> = {
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
            {text: 'Migration', link: '/guide/migration'},
            {text: 'Tutorials', link: '/tutorials/'},
            {
                text: 'Discord | Join Chat',
                link: 'https://discord.gg/WMxrvegf8H'
            },
            {text: 'Support Plans', link: 'https://www.jasperfx.net/support-plans/'}
        ],

        // algolia: {
        //     appId: 'IS2ZRHIXW9',
        //     apiKey: 'c8a9f5cb4e0f80733d0dadb4ae8d06ad',
        //     indexName: 'wolverine_index'
        // },

        search: {
      	    provider: 'local'
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
                    text: 'Introduction',
                    collapsed: false,
                    items: [
                        {text: 'What is Wolverine?', link: '/introduction/what-is-wolverine'},
                        {text: 'Getting Started', link: '/introduction/getting-started'},
                        {text: 'Support Policy', link: '/introduction/support-policy'},
                        {text: 'Wolverine for MediatR Users', link: '/introduction/from-mediatr'},
                        {text: 'Best Practices', link: '/introduction/best-practices'}, 
                    ]
                },
                {
                    text: 'Tutorials',
                    collapsed: true,
                    items: [                    
                        {text: 'Wolverine as Mediator', link: '/tutorials/mediator'},
                        {text: 'Ping/Pong Messaging', link: '/tutorials/ping-pong'},
                        {text: 'Custom Middleware', link: '/tutorials/middleware'},                                  
                        {text: 'Vertical Slice Architecture', link: '/tutorials/vertical-slice-architecture'},
                        {text: 'Modular Monoliths', link: '/tutorials/modular-monolith'},
                        {text: 'Event Sourcing and CQRS with Marten', link: '/tutorials/cqrs-with-marten'},
                        {text: 'Railway Programming with Wolverine', link: '/tutorials/railway-programming'},
                        {text: 'Interoperability with Non-Wolverine Systems', link: '/tutorials/interop'},
                        {text: 'Leader Election and Agents', link: '/tutorials/leader-election'},
                        {text: 'Dealing with Concurrency', link:' /tutorials/concurrency'}
                    ]
                },
                {
                    text: 'General',
                    collapsed: true,
                    items: [
                        {text: 'Basic Concepts', link: '/guide/basics'},
                        {text: 'Configuration', link: '/guide/configuration'},
                        {text: 'Runtime Architecture', link: '/guide/runtime'},
                        {text: 'Instrumentation and Metrics', link: '/guide/logging'},
                        {text: 'Diagnostics', link: '/guide/diagnostics'},
                        {text: 'Serverless Hosting', link: '/guide/serverless'},          
                        {text: 'Test Automation Support', link: '/guide/testing'},
                        {text: 'Command Line Integration', link: '/guide/command-line'},
                        {text: 'Code Generation', link: '/guide/codegen'},
                        {text: 'Extensions', link: '/guide/extensions'},
                        {text: 'Sample Projects', link: '/guide/samples'}
                    ]
                },
                {
                    text: 'Messages and Handlers',
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
                                {text: 'Fluent Validation Middleware', link: '/guide/handlers/fluent-validation'},
                                {text: 'Sticky Handler to Endpoint Assignments', link: '/guide/handlers/sticky'},
                                {text: 'Message Batching', link: '/guide/handlers/batching'},
                                {text: 'Persistence Helpers', link: '/guide/handlers/persistence'}
                            ]
                        },
                    ]
                },
                {
                    text: 'Messaging',
                    collapsed: true,
                    items: [
                        {text: 'Introduction to Messaging', link: '/guide/messaging/introduction'},
                        {text: 'Sending Messages', link: '/guide/messaging/message-bus'},
                        {text: 'Message Routing', link: '/guide/messaging/subscriptions'},
                        {text: 'Listening Endpoints', link: '/guide/messaging/listeners'},
                        {
                            text: 'Transports',
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
                                        {text: 'Interoperability', link:'/guide/messaging/transports/rabbitmq/interoperability'},
                                        {text: 'Connecting to Multiple Brokers', link: '/guide/messaging/transports/rabbitmq/multiple-brokers'},
                                        {text: 'Multi-Tenancy', link: '/guide/messaging/transports/rabbitmq/multi-tenancy'}
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
                                        {text: 'Scheduled Delivery', link: '/guide/messaging/transports/azureservicebus/scheduled'},
                                        {text: 'Multi-Tenancy', link: '/guide/messaging/transports/azureservicebus/multi-tenancy'}
                                    ]},
                                {text: 'Amazon SQS', link: '/guide/messaging/transports/sqs/', items:[
                                        {text: 'Publishing', link:'/guide/messaging/transports/sqs/publishing'},
                                        {text: 'Listening', link:'/guide/messaging/transports/sqs/listening'},
                                        {text: 'Dead Letter Queues', link:'/guide/messaging/transports/sqs/deadletterqueues'},
                                        {text: 'Configuring Queues', link:'/guide/messaging/transports/sqs/queues'},
                                        {text: 'Conventional Routing', link:'/guide/messaging/transports/sqs/conventional-routing'},
                                        {text: 'Interoperability', link:'/guide/messaging/transports/sqs/interoperability'},
                                        {text: 'MessageAttributes', link:'/guide/messaging/transports/sqs/message-attributes'}
                                    ]},
                                {text: 'Amazon SNS', link: '/guide/messaging/transports/sns'},
                                {text: 'TCP', link: '/guide/messaging/transports/tcp'},
                                {text: 'Google PubSub', link: '/guide/messaging/transports/gcp-pubsub/', items: [
                                        {text: 'Publishing', link:'/guide/messaging/transports/gcp-pubsub/publishing'},
                                        {text: 'Listening', link:'/guide/messaging/transports/gcp-pubsub/listening'},
                                        {text: 'Dead Letter Queues', link:'/guide/messaging/transports/gcp-pubsub/deadlettering'},
                                        {text: 'Conventional Routing', link:'/guide/messaging/transports/gcp-pubsub/conventional-routing'},
                                        {text: 'Interoperability', link:'/guide/messaging/transports/gcp-pubsub/interoperability'}
                                    ]},
                                {text: 'Apache Pulsar', link: '/guide/messaging/transports/pulsar'},
                                {text: 'Sql Server', link: '/guide/messaging/transports/sqlserver'},
                                {text: 'PostgreSQL', link: '/guide/messaging/transports/postgresql'},
                                {text: 'MQTT', link: '/guide/messaging/transports/mqtt'},
                                {text: 'Kafka', link: '/guide/messaging/transports/kafka'},
                                {text: 'External Database Tables', link: '/guide/messaging/transports/external-tables'}
                            ]
                        },
                        {text: 'Partitioned Sequential Messaging', link: '/guide/messaging/partitioning'},
                        {text: 'Endpoint Specific Operations', link: '/guide/messaging/endpoint-operations'},
                        {text: 'Broadcast to a Specific Topic', link: '/guide/messaging/broadcast-to-topic'},
                        {text: 'Message Expiration', link: '/guide/messaging/expiration'},
                        {text: 'Endpoint Policies', link: '/guide/messaging/policies'}
                    ]
                },
                {
                    text: 'ASP.Net Core Integration',
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
                        {text: 'HTTP Form Data', link: '/guide/http/forms'},
                        {text: `AsParameters Binding`, link: '/guide/http/as-parameters'},
                        {text: 'Middleware', link: '/guide/http/middleware.md'},
                        {text: 'Policies', link: '/guide/http/policies.md'},
                        {text: 'OpenAPI Metadata', link: '/guide/http/metadata'},
                        {text: 'Using as Mediator', link: '/guide/http/mediator'},
                        {text: 'Multi-Tenancy and ASP.Net Core', link: '/guide/http/multi-tenancy'},
                        {text: 'Publishing Messages', link: '/guide/http/messaging'},
                        {text: 'Uploading Files', link: '/guide/http/files'},
                        {text: 'Integration with Sagas', link: '/guide/http/sagas'},
                        {text: 'Integration with Marten', link: '/guide/http/marten'},
                        {text: 'Fluent Validation', link: '/guide/http/fluentvalidation'},
                        {text: 'Problem Details', link: '/guide/http/problemdetails'},
                    ]
                },
                {
                    text: 'Durability and Persistence',
                    collapsed: true,
                    items: [
                        {text: 'Durable Inbox and Outbox Messaging', link: '/guide/durability/'},
                        {text: 'Troubleshooting and Leadership Election', link: '/guide/durability/leadership-and-troubleshooting'},
                        {text: 'Sagas', link: '/guide/durability/sagas'},
                        {text: 'Marten Integration', link: '/guide/durability/marten/',  collapsed: false, items: [
                                {text: 'Transactional Middleware', link: '/guide/durability/marten/transactional-middleware'},
                                {text: 'Transactional Outbox Support', link: '/guide/durability/marten/outbox'},
                                {text: 'Transactional Inbox Support', link: '/guide/durability/marten/inbox'},
                                {text: 'Operation Side Effects', link: '/guide/durability/marten/operations'},
                                {text: 'Aggregate Handlers and Event Sourcing', link: '/guide/durability/marten/event-sourcing'},
                                {text: 'Event Forwarding to Wolverine', link: '/guide/durability/marten/event-forwarding'},
                                {text: 'Event Subscriptions', link: '/guide/durability/marten/subscriptions'},
                                {text: 'Subscription/Projection Distribution', link: '/guide/durability/marten/distribution'},
                                {text: 'Sagas', link: '/guide/durability/marten/sagas'},
                                {text: 'Multi-Tenancy and Marten', link: '/guide/durability/marten/multi-tenancy'},
                                {text: 'Ancillary Marten Stores', link: '/guide/durability/marten/ancillary-stores'},
                            ]},
                        {text: 'Sql Server Integration', link: '/guide/durability/sqlserver'},
                        {text: 'PostgreSQL Integration', link: '/guide/durability/postgresql'},
                        {text: 'RavenDb Integration', link: '/guide/durability/ravendb'},
                        {text: 'Entity Framework Core Integration', collapsed: false, link: '/guide/durability/efcore', items: [
                                {text: 'Transactional Middleware', link: '/guide/durability/efcore/transactional-middleware'},
                                {text: 'Transactional Inbox and Outbox', link: '/guide/durability/efcore/outbox-and-inbox'},
                                {text: 'Operation Side Effects', link: '/guide/durability/efcore/operations'},
                                {text: 'Saga Storage', link: '/guide/durability/efcore/sagas'},
                                {text: 'Multi-Tenancy', link: '/guide/durability/efcore/multi-tenancy'}
                            
                            ]},
                        {text: 'Managing Message Storage', link: '/guide/durability/managing'},
                        {text: 'Dead Letter Storage', link: '/guide/durability/dead-letter-storage'},
                        {text: 'Idempotent Message Delivery', link:'/guide/durability/idempotency'}
                    ]
                },

            ]
        }
    },
    markdown: {
        linkify: false,
        config: (md) => {
            md.use(blockEmbedPlugin)
        }
    },
    ignoreDeadLinks: true,
    vite: {
        plugins: [llmstxt()]
    }
}

export default defineConfig(withMermaid(config));
