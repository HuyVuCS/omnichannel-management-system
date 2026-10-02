import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { createAppRouter } from './app/router';
import { createAppStore } from './app/store';
import { loadActiveLanguage } from './shared/i18n';
import './index.css';

const container = document.getElementById('root');
if (!container) {
  throw new Error('Root element #root is missing in index.html.');
}

// The texts of the language in use are a chunk of their own: they are loaded before the first render, so no page ever
// shows translation keys.
loadActiveLanguage().then(
  () => {
    createRoot(container).render(
      <StrictMode>
        <App store={createAppStore()} router={createAppRouter()} />
      </StrictMode>,
    );
  },
  () => {
    // Without texts nothing can be rendered or translated (a redeploy while the page loaded, or the network failed):
    // the one message of the application that exists in both languages at once.
    container.textContent =
      'Không tải được ứng dụng. Vui lòng tải lại trang. / The application could not be loaded. Please reload the page.';
  },
);
