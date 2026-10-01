// AntD v5 needs this compatibility patch on React 19 (see the AntD v5 documentation).
import '@ant-design/v5-patch-for-react-19';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { createAppRouter } from './app/router';
import { createAppStore } from './app/store';
import './index.css';

const container = document.getElementById('root');
if (!container) {
  throw new Error('Root element #root is missing in index.html.');
}

createRoot(container).render(
  <StrictMode>
    <App store={createAppStore()} router={createAppRouter()} />
  </StrictMode>,
);
