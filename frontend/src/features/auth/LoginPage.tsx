import { Alert, Button, Card, Form, Input, Typography } from 'antd';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate, useSearchParams } from 'react-router';
import { applyProblemToForm } from '@/shared/api/problemDetails';
import { isApiError } from '@/shared/api/types';
import { LanguageSwitch } from '@/shared/components/LanguageSwitch';
import { useAppDispatch, useAppSelector } from '@/shared/hooks/store';
import { useHeadingLook } from '@/shared/hooks/useHeadingLook';
import { useLoginMutation, type LoginRequest } from './authApi';
import { selectSession, sessionStarted } from './authSlice';
import { RETURN_URL_PARAM, safeReturnUrl } from './returnUrl';

const CARD_WIDTH = 380;

/** Translation keys, not texts: the message follows a later language switch. */
type LoginFailure = 'auth.invalidCredentials' | 'auth.tooManyAttempts';

export function LoginPage() {
  const { t } = useTranslation();
  const dispatch = useAppDispatch();
  const session = useAppSelector(selectSession);
  const [searchParams] = useSearchParams();
  const [form] = Form.useForm<LoginRequest>();
  const [login, { isLoading }] = useLoginMutation();
  const [failure, setFailure] = useState<LoginFailure | null>(null);
  const [formErrors, setFormErrors] = useState<string[]>([]);
  // The page's one <h1>, with the look of the level-3 heading it was.
  const titleLook = useHeadingLook(3);

  if (session) {
    return <Navigate to={safeReturnUrl(searchParams.get(RETURN_URL_PARAM))} replace />;
  }

  const submit = async (values: LoginRequest) => {
    setFailure(null);
    setFormErrors([]);
    try {
      const result = await login(values).unwrap();
      dispatch(sessionStarted({ accessToken: result.accessToken, expiresAt: result.expiresAt, user: result.user }));
    } catch (error) {
      if (isApiError(error) && error.status === 401) {
        // One generic message: the API does not reveal whether the account exists or is locked either.
        setFailure('auth.invalidCredentials');
      } else if (isApiError(error) && error.status === 429) {
        setFailure('auth.tooManyAttempts');
      } else {
        setFormErrors(applyProblemToForm(form, error, t));
      }
    }
  };

  return (
    <div
      style={{
        minHeight: '100vh',
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 16,
        padding: 16,
      }}
    >
      <div style={{ width: CARD_WIDTH, maxWidth: '100%', display: 'flex', justifyContent: 'flex-end' }}>
        <LanguageSwitch />
      </div>
      <Card style={{ width: CARD_WIDTH, maxWidth: '100%' }}>
        <Typography.Title level={1} style={{ ...titleLook, marginTop: 0 }}>
          {t('auth.title')}
        </Typography.Title>
        {failure && <Alert type="error" showIcon title={t(failure)} style={{ marginBottom: 16 }} />}
        {formErrors.length > 0 && (
          <Alert type="error" showIcon title={formErrors.join(' ')} style={{ marginBottom: 16 }} />
        )}
        <Form form={form} layout="vertical" requiredMark={false} onFinish={(values) => void submit(values)}>
          <Form.Item name="email" label={t('auth.email')} rules={[{ required: true }, { type: 'email' }]}>
            <Input autoComplete="username" autoFocus />
          </Form.Item>
          <Form.Item name="password" label={t('auth.password')} rules={[{ required: true }]}>
            <Input.Password autoComplete="current-password" />
          </Form.Item>
          <Button type="primary" htmlType="submit" loading={isLoading} block>
            {t('auth.submit')}
          </Button>
        </Form>
      </Card>
    </div>
  );
}
