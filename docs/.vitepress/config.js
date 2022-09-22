import { BUNDLED_LANGUAGES } from 'shiki'

// Include `cs` as alias for csharp
BUNDLED_LANGUAGES
  .find(lang => lang.id === 'csharp').aliases.push('cs');

module.exports = {
    base: '/',
    lang: 'en-US',
    title: 'Wolverine',
    description: 'Next Generation Command and Message Bus for .NET',
    head: [
      ['link', { rel: 'apple-touch-icon', type: 'image/png', size: "180x180", href: '/apple-icon-180x180.png' }],
      ['link', { rel: 'icon', type: 'image/png', size: "32x32", href: '/favicon-32x32.png' }],
      ['link', { rel: 'icon', type: 'image/png', size: "16x16", href: '/favicon-16x16.png' }],
      ['link', { rel: 'manifest', manifest: '/manifest.json' }],
    ],
    lastUpdated: true,
    themeConfig: {
        logo: '/logo.png',

        nav: [
            { text: 'Guide', link: '/guide/' },
            { text: 'Gitter | Join Chat', link: 'https://gitter.im/JasperFx/wolverine?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge' }
        ],

        algolia: {
            appId: '2V5OM390DF',
            apiKey: '674cd4f3e6b6ebe232a980c7cc5a0270',
            indexName: 'wolverine_index'
        },

        editLink: {
          pattern: 'https://github.com/JasperFx/wolverine/edit/main/docs/:path',
          text: 'Suggest changes to this page'
        },

        footer: {
          message: 'Released under the MIT License.',
          copyright: 'Copyright © Jeremy D. Miller and contributors.',
        },

        sidebar: {
          '/': [
            {
              text: 'Introduction',
              collapsible: true,
              items: [ {text: 'What is Wolverine?', link: '/guide/'} ]
            },
            {
              text: 'Mediator',
              collapsible: true,
              items: [ {text: 'Use as Mediator', link: '/guide/mediator'} ]
            },
            {
              text: 'Command Bus',
              collapsible: true,
              items: [ {text: 'Use as Command Bus', link: '/guide/in-memory-bus'} ]
            },
            {
              text: 'Messaging Bus',
              collapsible: true,
              items: [ 
                {text: 'Use as Messaging Bus', link: '/guide/messaging/'},
                {text: 'Configuring Messaging', link: '/guide/messaging/configuration'},
                {text: 'Message Routing', link: '/guide/messaging/routing'},
                {text: 'Publishing and Sending', link: '/guide/messaging/pubsub'},
                {text: 'Message Expiration', link: '/guide/messaging/expiration'},
                {text: 'Transports', link: '/guide/messaging/transports/'},
                {text: 'Rabbit MQ Transport', link: '/guide/messaging/transports/rabbitmq'},
                {text: 'Pulsar Transport', link: '/guide/messaging/transports/pulsar'},
                {text: 'TCP Transport', link: '/guide/messaging/transports/tcp'},
                {text: 'MassTransit Interop', link: '/guide/messaging/transports/masstransit'},
                {text: 'Scheduled Delivery', link: '/guide/messaging/scheduled'},
                {text: 'Message Correlation', link: '/guide/messaging/correlation'}
              ]
            },
            {
              text: 'Durable Messaging',
              collapsible: true,
              items: [ 
                {text: 'Durable Inbox and Outbox Messaging', link: '/guide/durability/'},
                {text: 'Stateful Sagas', link: '/guide/durability/sagas'},
                {text: 'Stateful Sagas using Marten', link: '/guide/durability/marten'},
                {text: 'Stateful Sagas using Entity Framework Core', link: '/guide/durability/efcore'}
              ]
            },
            {
              text: 'General',
              collapsible: true,
              items: [ 
                {text: 'Messages and Serialization', link: '/guide/messages'},
                {text: 'Scheduled', link: '/guide/scheduled'},
                {text: 'Configuration', link: '/guide/configuration'},
                {text: 'Instrumentation, Diagnostics, and Logging', link: '/guide/logging'},
                {text: 'Test Automation Support', link: '/guide/testing'},
                {text: 'Command Line Integration', link: '/guide/command-line'},
                {text: 'Best Practices', link: '/guide/best-practices'},
                {text: 'Extensions', link: '/guide/extensions'}
                // {text: 'Alba Setup', link: '/guide/hosting'},
                // {text: 'Integrating with xUnit.Net', link: '/guide/xunit'},
                // {text: 'Integrating with NUnit', link: '/guide/nunit'},
                // {text: 'Extension Model', link: '/guide/extensions'},
                // {text: 'Security Extensions', link: '/guide/security'}
              ]
            },
          ]
        }
    },
    markdown: {
        linkify: false
    }
}

