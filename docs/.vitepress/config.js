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
                        {text: 'Custom Middleware', link: '/tutorials/middleware'}
                    ]
                },
                {
                    text: 'General',
                    collapsible: true,
                    collapsed: true,
                    items: [
                        {text: 'Basic Concepts', link: '/guide/basics'},
                        {text: 'Configuration', link: '/guide/configuration'},
                        {text: 'Instrumentation and Metrics', link: '/guide/logging'},
                        {text: 'Diagnostics', link: '/guide/diagnostics'},
                        {text: 'Test Automation Support', link: '/guide/testing'},
                        {text: 'Command Line Integration', link: '/guide/command-line'},
                        {text: 'Runtime Architecture', link: '/guide/runtime'},
                        {text: 'Code Generation', link: '/guide/codegen'},
                        {text: 'Extensions', link: '/guide/extensions'}
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
                                {text: 'Cascading Messages', link: '/guide/handlers/cascading'},
                                {text: 'Middleware', link: '/guide/handlers/middleware'},
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
                        {text: 'Working with IMessageBus', link: '/guide/messaging/message-bus'},
                        {text: 'Subscriptions & Message Routing', link: '/guide/messaging/subscriptions'},
                        {text: 'Message Listeners', link: '/guide/messaging/listeners'},
                        {
                            text: 'Transports',
                            collapsible: true,
                            collapsed: true,
                            items: [
                                {text: 'Local Queues', link: '/guide/messaging/transports/local'},
                                {text: 'Rabbit MQ', link: '/guide/messaging/transports/rabbitmq'},
                                {text: 'Azure Service Bus', link: '/guide/messaging/transports/azure-service-bus'},
                                {text: 'Amazon SQS', link: '/guide/messaging/transports/sqs'},
                                {text: 'TCP', link: '/guide/messaging/transports/tcp'},
                                {text: 'Pulsar', link: '/guide/messaging/transports/pulsar'},
                            ]
                        },
                        {text: 'Endpoint Specific Operations', link: '/guide/messaging/endpoint-operations'},
                        {text: 'Broadcast to a Specific Topic', link: '/guide/messaging/broadcast-to-topic'},
                        {text: 'Message Expiration', link: '/guide/messaging/expiration'},
                        {text: 'Interoperability with NServiceBus', link: '/guide/messaging/nservicebus'},
                        {text: 'Interoperability with MassTransit', link: '/guide/messaging/masstransit'},
                    ]
                },
                {
                    text: 'ASP.Net Core Integration',
                    collapsible: true,
                    collapsed: true,
                    items: [
                        {text: 'HTTP Services', link: '/guide/http/'},
                        {text: 'Endpoints', link: '/guide/http/endpoints'},
                        {text: 'Middleware', link: '/guide/http/middleware.md'},
                        {text: 'Policies', link: '/guide/http/policies.md'}
                    ]
                },
                {
                    text: 'Durability and Persistence',
                    collapsible: true,
                    collapsed: true,
                    items: [
                        {text: 'Durable Inbox and Outbox Messaging', link: '/guide/durability/'},
                        {text: 'Sagas', link: '/guide/durability/sagas'},
                        {text: 'Marten Integration', link: '/guide/durability/marten'},
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

