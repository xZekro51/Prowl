# Prowl Hub Window

This document describes the new ProwlHubWindow that has been added to the Prowl Game Engine editor, which provides a Unity Hub-style interface for project management.

## Overview

The ProwlHubWindow is a modern, user-friendly interface that mimics the design and functionality of Unity Hub. It provides a centralized location for managing projects, with additional tabs planned for future features like installs, learning resources, community, and settings.

## Features

### Main Interface
- **Modern Design**: Clean, dark theme that matches the existing Prowl editor style
- **Tabbed Navigation**: Sidebar with tabs for different sections (Projects, Installs, Learn, Community, Settings)
- **Title Bar**: Custom title bar with window controls (minimize, close)

### Projects Tab
- **Table View**: Projects are displayed in a sortable table format with the following columns:
  - Star (for favoriting projects - UI ready, functionality pending)
  - Cloud sync status (UI ready, functionality pending)  
  - Project type indicator
  - Project name and path
  - Last modified date
  - Editor version
  - Actions menu (three-dot menu)

- **Search Functionality**: Real-time search filtering of projects by name
- **Sorting**: Click column headers to sort by Name, Modified date, or Editor version
- **Project Actions**: Right-click context menu with options to:
  - Open project
  - Show in Explorer/Finder
  - Remove from list

### Project Creation
- **New Project Sidebar**: Slides out from the right when creating a new project
- **Project Templates**: Visual preview of project templates (currently shows placeholder)
- **Location Selection**: Browse and select where to create the new project
- **Input Validation**: Ensures valid project names and paths

### Additional Features
- **Double-click to Open**: Double-click any project row to open it immediately
- **Keyboard Navigation**: Escape key to close creation sidebar
- **Visual Feedback**: Hover states, selection highlighting, and loading states
- **Responsive Design**: Adapts to different window sizes

## Integration

### Startup Integration
The ProwlHubWindow automatically opens when the editor starts without a project loaded (replacing the previous ProjectsWindow).

### Menu Integration
A new "File > Hub" menu item has been added to allow opening the Hub window from within the editor.

### Project Cache Integration
The window integrates with the existing ProjectCache system to:
- Load and display recent projects
- Add new projects to the cache
- Remove projects from the cache
- Remember project locations and settings

## Future Enhancements

The framework is in place for additional tabs:

1. **Installs Tab**: For managing different editor versions and plugins
2. **Learn Tab**: For tutorials, documentation, and learning resources
3. **Community Tab**: For community features, sharing, and collaboration
4. **Settings Tab**: For global editor preferences and account settings

## Technical Details

### File Structure
- `Prowl.Editor/Editor/ProwlHubWindow.cs`: Main window implementation
- Integration points in `Program.cs` and `EditorGuiManager.cs`

### Dependencies
- Uses existing Prowl GUI system
- Integrates with ProjectCache for project management
- Uses FontAwesome6 icons for consistent iconography
- Follows EditorStylePrefs for theming

### Architecture
The window follows the established Prowl editor patterns:
- Inherits from EditorWindow
- Uses the Prowl GUI layout system
- Implements proper disposal and cleanup
- Follows the existing coding conventions

## Usage

1. **Starting Without Project**: The Hub window opens automatically
2. **Opening from Editor**: Use File > Hub menu item
3. **Creating Projects**: Click "New project" button and fill in details
4. **Opening Projects**: Double-click or use context menu
5. **Managing Projects**: Use search, sorting, and context menus

The ProwlHubWindow provides a professional, modern interface that enhances the user experience and makes project management more intuitive and efficient.
