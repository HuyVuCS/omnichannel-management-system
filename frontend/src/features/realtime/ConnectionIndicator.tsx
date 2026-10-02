import { DisconnectOutlined, SyncOutlined, WifiOutlined } from '@ant-design/icons';
import { theme, Tooltip, Typography } from 'antd';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { CONNECTION_INDICATOR_WIDTH } from '@/shared/components/layout';
import { useAppSelector } from '@/shared/hooks/store';
import { selectConnectionState, type ConnectionState } from './realtimeSlice';

/**
 * Tells the user whether the screen is live: icon and text (never colour alone), with a tooltip saying what it means
 * for the data. Fixed width, so neither a state change nor a language switch moves the header.
 */
export function ConnectionIndicator() {
  const { t } = useTranslation();
  const { token } = theme.useToken();
  const state = useAppSelector(selectConnectionState);

  const icons: Record<ConnectionState, ReactNode> = {
    connected: <WifiOutlined style={{ color: token.colorSuccess }} />,
    connecting: <SyncOutlined spin style={{ color: token.colorWarning }} />,
    reconnecting: <SyncOutlined spin style={{ color: token.colorWarning }} />,
    disconnected: <DisconnectOutlined style={{ color: token.colorError }} />,
  };

  return (
    <Tooltip title={t(`realtime.tooltip.${state}`)}>
      <span
        role="status"
        data-testid="connection-indicator"
        data-state={state}
        style={{
          width: CONNECTION_INDICATOR_WIDTH,
          flex: 'none',
          display: 'inline-flex',
          alignItems: 'center',
          gap: 6,
          overflow: 'hidden',
        }}
      >
        {icons[state]}
        <Typography.Text ellipsis style={{ minWidth: 0 }}>
          {t(`realtime.state.${state}`)}
        </Typography.Text>
      </span>
    </Tooltip>
  );
}
