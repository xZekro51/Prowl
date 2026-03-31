// @ts-check

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'Prowl Engine',
  tagline: 'An Open Source Unity-like Game Engine built in pure C#',
  favicon: 'img/favicon.ico',

  url: 'https://prowlengine.github.io',
  baseUrl: '/Prowl/',

  organizationName: 'ProwlEngine',
  projectName: 'Prowl',

  onBrokenLinks: 'throw',

  markdown: {
    mermaid: true,
    hooks: {
      onBrokenMarkdownLinks: 'warn',
    },
  },

  themes: ['@docusaurus/theme-mermaid'],

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          sidebarPath: './sidebars.js',
          editUrl: 'https://github.com/ProwlEngine/Prowl/tree/main/website/',
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      }),
    ],
  ],

  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      image: 'img/prowl-social-card.png',
      announcementBar: {
        id: 'early_development',
        content:
          '🚧 Prowl is in early development — <a href="https://github.com/ProwlEngine/Prowl">star us on GitHub</a> and <a href="https://discord.gg/BqnJ9Rn4sn">join the Discord</a> to follow along!',
        backgroundColor: '#6c63ff',
        textColor: '#fff',
        isCloseable: true,
      },
      navbar: {
        title: 'Prowl Engine',
        logo: {
          alt: 'Prowl Engine Logo',
          src: 'img/logo.svg',
        },
        items: [
          {
            type: 'docSidebar',
            sidebarId: 'docsSidebar',
            position: 'left',
            label: 'Documentation',
          },
          {
            href: 'https://github.com/ProwlEngine/Prowl',
            label: 'GitHub',
            position: 'right',
          },
          {
            href: 'https://discord.gg/BqnJ9Rn4sn',
            label: 'Discord',
            position: 'right',
          },
        ],
      },
      footer: {
        style: 'dark',
        links: [
          {
            title: 'Documentation',
            items: [
              {
                label: 'Getting Started',
                to: '/docs/getting-started',
              },
              {
                label: 'Event System',
                to: '/docs/architecture/event-system',
              },
            ],
          },
          {
            title: 'Community',
            items: [
              {
                label: 'Discord',
                href: 'https://discord.gg/BqnJ9Rn4sn',
              },
              {
                label: 'GitHub Issues',
                href: 'https://github.com/ProwlEngine/Prowl/issues',
              },
            ],
          },
          {
            title: 'More',
            items: [
              {
                label: 'GitHub',
                href: 'https://github.com/ProwlEngine/Prowl',
              },
              {
                label: 'Contributing',
                to: '/docs/contributing',
              },
            ],
          },
        ],
        copyright: `Copyright © ${new Date().getFullYear()} Prowl Engine Contributors. Built with Docusaurus.`,
      },
      prism: {
        theme: require('prism-react-renderer').themes.github,
        darkTheme: require('prism-react-renderer').themes.dracula,
        additionalLanguages: ['csharp', 'glsl', 'bash'],
      },
      colorMode: {
        defaultMode: 'dark',
        disableSwitch: false,
        respectPrefersColorScheme: true,
      },
      mermaid: {
        theme: { light: 'default', dark: 'dark' },
      },
    }),
};

module.exports = config;
