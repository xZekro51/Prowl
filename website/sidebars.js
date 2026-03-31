/** @type {import('@docusaurus/plugin-content-docs').SidebarsConfig} */
const sidebars = {
  docsSidebar: [
    'intro',
    'getting-started',
    'contributing',
    {
      type: 'category',
      label: 'Architecture',
      items: [
        'architecture/event-system',
        'architecture/vulkan-rendering-pipeline',
      ],
    },
    {
      type: 'category',
      label: 'Reviews',
      items: [
        'reviews/event-system-review',
        'reviews/vulkan-pipeline-review',
      ],
    },
    {
      type: 'category',
      label: 'Features',
      items: [
        'features/rendering',
        'features/physics',
        'features/scripting',
        'features/asset-pipeline',
      ],
    },
    'roadmap',
  ],
};

module.exports = sidebars;
