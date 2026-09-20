import { createRoot } from 'react-dom/client';
import App from './App';
import { SearchPanel } from './components/SearchPanel';
import './styles.css';
createRoot(document.getElementById('root')!).render(new URLSearchParams(location.search).get('view') === 'search' ? <SearchPanel/> : <App/>);
