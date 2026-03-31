import clsx from 'clsx';
import Link from '@docusaurus/Link';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import Layout from '@theme/Layout';
import Heading from '@theme/Heading';
import styles from './index.module.css';

function HomepageHeader() {
  const { siteConfig } = useDocusaurusContext();
  return (
    <header className={clsx('hero hero--primary', styles.heroBanner)}>
      <div className="container">
        <Heading as="h1" className="hero__title">
          🎮 {siteConfig.title}
        </Heading>
        <p className="hero__subtitle">{siteConfig.tagline}</p>
        <div className={styles.buttons}>
          <Link
            className="button button--secondary button--lg"
            to="/docs/getting-started">
            Get Started →
          </Link>
          <Link
            className="button button--secondary button--lg"
            style={{ marginLeft: '1rem' }}
            href="https://github.com/ProwlEngine/Prowl">
            GitHub ⭐
          </Link>
        </div>
      </div>
    </header>
  );
}

const features = [
  {
    title: 'Unity-like API',
    description:
      'Familiar GameObject & Component architecture with C# scripting. Seamless transition for Unity developers.',
  },
  {
    title: 'Cross-Platform',
    description:
      'Runs on Windows, Linux, and macOS. Build standalone applications for all three platforms.',
  },
  {
    title: 'Modern .NET 9',
    description:
      'Built with the latest .NET, leveraging source generators, Span<T>, and modern C# features for performance.',
  },
  {
    title: 'Multiple Rendering Backends',
    description:
      'OpenGL, Vulkan, Metal, and DirectX 11 support through the Graphite abstraction layer.',
  },
  {
    title: 'PBR Rendering',
    description:
      'Deferred rendering pipeline with HDR, physically-based materials, shadow mapping, and post-processing.',
  },
  {
    title: 'Open Source',
    description:
      'MIT licensed. Free to use, modify, and distribute. Community-driven development.',
  },
];

function Feature({ title, description }) {
  return (
    <div className={clsx('col col--4')}>
      <div className="text--center padding-horiz--md padding-vert--md">
        <Heading as="h3">{title}</Heading>
        <p>{description}</p>
      </div>
    </div>
  );
}

function HomepageFeatures() {
  return (
    <section className={styles.features}>
      <div className="container">
        <div className="row">
          {features.map((props, idx) => (
            <Feature key={idx} {...props} />
          ))}
        </div>
      </div>
    </section>
  );
}

export default function Home() {
  const { siteConfig } = useDocusaurusContext();
  return (
    <Layout
      title={`${siteConfig.title} — Documentation`}
      description="Documentation for the Prowl open-source game engine built in pure C#">
      <HomepageHeader />
      <main>
        <HomepageFeatures />
      </main>
    </Layout>
  );
}
